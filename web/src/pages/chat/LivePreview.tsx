// 正在填的这份文档，边说边看它长出来。
// 选完基准、答完一项、采纳一条建议之后，右边这块立刻跟着变——
// 不用等「生成文档」才知道填成了什么样，也不用切到逐项核对台去看。
import { useEffect, useRef, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { get } from '../../lib/api';
import { SOURCE_LABEL, SOURCE_PILL_STYLE } from '../../lib/types';
import type { PreviewView, SlotFillSource } from '../../lib/types';
import { Spinner } from '../../components/Common';

export function LivePreview({ sessionId, title, revision, onClose }: {
  sessionId: string;
  title: string | null;
  /** 每完成一轮 +1，用来触发重取 */
  revision: number;
  onClose: () => void;
}) {
  const q = useQuery({
    queryKey: ['chat-preview', sessionId, revision],
    queryFn: () => get<PreviewView>(`/api/generate/sessions/${sessionId}/preview`),
  });

  // 这一轮改动了哪几项：跟上一次的快照比，短暂点亮，让人看得见「刚才那句落在哪」
  const prev = useRef<Map<string, string | null>>(new Map());
  const [flash, setFlash] = useState<Set<string>>(new Set());
  useEffect(() => {
    if (!q.data) return;
    const now = new Map<string, string | null>();
    for (const sec of q.data.sections) for (const it of sec.items) now.set(it.tag, it.value);
    if (prev.current.size > 0) {
      const changed = new Set<string>();
      for (const [tag, val] of now) if (prev.current.get(tag) !== val) changed.add(tag);
      if (changed.size > 0) {
        setFlash(changed);
        const t = setTimeout(() => setFlash(new Set()), 2400);
        prev.current = now;
        return () => clearTimeout(t);
      }
    }
    prev.current = now;
  }, [q.data]);

  const all = q.data?.sections.flatMap((s) => s.items) ?? [];
  const done = all.filter((i) => i.value).length;

  return (
    <aside className="lp">
      <div className="lp-hd">
        <span className="lp-t" title={title ?? undefined}>{title ?? '当前文档'}</span>
        <span className="pill pill-neutral m">{done}/{all.length}</span>
        <button className="gbtn lp-x" title="收起预览" onClick={onClose}>收起</button>
      </div>
      <div className="lp-body sc">
        {q.isPending && <div style={{ padding: 12 }}><Spinner text="取预览…" /></div>}
        {q.isError && <div className="hint" style={{ padding: 12 }}>预览暂时取不到，不影响继续填。</div>}
        {q.data?.sections.map((sec) => (
          <div key={sec.section}>
            <div className="lp-sec">
              {sec.section}
              <span className="lp-cnt">{sec.items.filter((i) => i.value).length}/{sec.items.length}</span>
            </div>
            {sec.items.map((it) => (
              <div key={it.tag} className={`lp-row${it.value ? '' : ' blank'}${flash.has(it.tag) ? ' flash' : ''}`}>
                <div className="lp-name">{it.name}</div>
                <div className="lp-val">
                  {it.value ?? <span className="lp-empty">待填</span>}
                  {it.value && it.origin && (
                    <span className="pill lp-src" style={SOURCE_PILL_STYLE[it.origin as SlotFillSource]}>
                      {SOURCE_LABEL[it.origin as SlotFillSource] ?? it.sourceLabel}
                    </span>
                  )}
                </div>
              </div>
            ))}
          </div>
        ))}
      </div>
    </aside>
  );
}
