// 正在填的这份文档，边说边看它长出来。
//
// 默认看原件版式：每完成一轮，服务端按当前已填值渲染一份草稿 docx，
// 浏览器里用 docx-preview 还原（表格、字体、页边距都是模板本来的样子，不出网、不转换服务）。
// 想看每一项的来源（继承 / AI 建议待确认 / 已确认）就切到「字段」——
// 那些标记 Word 里没有，但决定了哪几项还需要人确认。
import { useEffect, useRef, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { fetchBlob, get } from '../../lib/api';
import { SOURCE_LABEL, SOURCE_PILL_STYLE } from '../../lib/types';
import type { PreviewView, SlotFillSource } from '../../lib/types';
import { ErrorBox, Spinner } from '../../components/Common';

type Mode = 'doc' | 'fields';

export function LivePreview({ sessionId, title, revision, onClose }: {
  sessionId: string;
  title: string | null;
  /** 每完成一轮 +1，用来触发重取 */
  revision: number;
  onClose: () => void;
}) {
  const [mode, setMode] = useState<Mode>('doc');
  const fields = useQuery({
    queryKey: ['chat-preview', sessionId, revision],
    queryFn: () => get<PreviewView>(`/api/generate/sessions/${sessionId}/preview`),
  });
  // 接口给的东西不合预期时也不能把整页带崩——预览只是个旁栏
  const sections = fields.data?.sections ?? [];
  const all = sections.flatMap((s) => s.items ?? []);
  const done = all.filter((i) => i.value).length;

  return (
    <aside className="lp">
      <div className="lp-hd">
        <span className="lp-t" title={title ?? undefined}>{title ?? '当前文档'}</span>
        {all.length > 0 && <span className="pill pill-neutral m">{done}/{all.length}</span>}
        <span className="lp-tabs">
          <button className={`lp-tab${mode === 'doc' ? ' on' : ''}`} onClick={() => setMode('doc')}>原件版式</button>
          <button className={`lp-tab${mode === 'fields' ? ' on' : ''}`} onClick={() => setMode('fields')}>字段</button>
        </span>
        <button className="gbtn lp-x" title="收起预览" onClick={onClose}>收起</button>
      </div>
      {mode === 'doc'
        ? <DocView sessionId={sessionId} revision={revision} />
        : <FieldView sections={sections} pending={fields.isPending} failed={fields.isError} />}
    </aside>
  );
}

/** 原件版式：把草稿 docx 渲染出来。渲染期间保留上一版画面，不闪。 */
function DocView({ sessionId, revision }: { sessionId: string; revision: number }) {
  const box = useRef<HTMLDivElement>(null);
  const [state, setState] = useState<'loading' | 'ok' | 'fail'>('loading');
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let alive = true;
    // 不先清空：新的一版渲染好再整体替换，避免每轮白一下
    setState((s) => (s === 'ok' ? 'ok' : 'loading'));
    setError(null);
    void (async () => {
      try {
        const blob = await fetchBlob(`/api/generate/sessions/${sessionId}/draft.docx`);
        const { renderAsync } = await import('docx-preview');
        if (!alive || !box.current) return;
        const stage = document.createElement('div');
        await renderAsync(blob, stage, undefined, {
          className: 'docx', inWrapper: true, breakPages: true,
          renderHeaders: true, renderFooters: true,
        });
        if (!alive || !box.current) return;
        box.current.replaceChildren(...Array.from(stage.childNodes));
        setState('ok');
      } catch (e) {
        if (!alive) return;
        setError(e instanceof Error ? e.message : '草稿渲染失败');
        setState('fail');
      }
    })();
    return () => { alive = false; };
  }, [sessionId, revision]);

  return (
    <div className="lp-body sc">
      {state === 'loading' && <div style={{ padding: 12 }}><Spinner text="按当前内容排版…" /></div>}
      {state === 'fail' && (
        <div style={{ padding: 12 }}>
          <ErrorBox message={error ?? '草稿渲染失败'} />
          <div className="hint" style={{ marginTop: 6 }}>不影响继续填，可以切到「字段」看已填内容。</div>
        </div>
      )}
      <div ref={box} className="lp-doc" />
    </div>
  );
}

/** 字段视图：Word 里看不到的来源标记在这儿——哪几项是继承的、哪几项还等人确认。 */
function FieldView({ sections, pending, failed }: {
  sections: PreviewView['sections']; pending: boolean; failed: boolean;
}) {
  // 这一轮改动了哪几项：跟上一次的快照比，短暂点亮，让人看得见「刚才那句落在哪」
  const prev = useRef<Map<string, string | null>>(new Map());
  const [flash, setFlash] = useState<Set<string>>(new Set());
  useEffect(() => {
    if (sections.length === 0) return;
    const now = new Map<string, string | null>();
    for (const sec of sections) for (const it of sec.items ?? []) now.set(it.tag, it.value);
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
  }, [sections]);

  return (
    <div className="lp-body sc">
      {pending && <div style={{ padding: 12 }}><Spinner text="取预览…" /></div>}
      {failed && <div className="hint" style={{ padding: 12 }}>预览暂时取不到，不影响继续填。</div>}
      {sections.map((sec) => (
        <div key={sec.section}>
          <div className="lp-sec">
            {sec.section}
            <span className="lp-cnt">{sec.items.filter((i) => i.value).length}/{sec.items.length}</span>
          </div>
          {(sec.items ?? []).map((it) => (
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
  );
}
