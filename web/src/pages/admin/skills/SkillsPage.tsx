// 后台「技能管理」：给助手的专家答题指南。左边按类列出全部技能，右边编辑选中的那个。
// 格式是开放标准 Agent Skills 的 SKILL.md（代号、什么时候用、正文），能导入社区的技能，也能导出带走。
import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { get } from '../../../lib/api';
import { Icon } from '../../../components/Icons';
import { Spinner, ErrorBox } from '../../../components/Common';
import { SOURCE, STATUS } from './types';
import type { SkillList, SkillMeta, SkillStatus } from './types';
import { SkillEditor } from './SkillEditor';
import { ImportBox } from './ImportBox';
import '../../../styles/quality.css';
import '../../../styles/skills.css';

export default function SkillsPage() {
  const list = useQuery({ queryKey: ['skills'], queryFn: () => get<SkillList>('/api/skills') });
  const meta = useQuery({ queryKey: ['skills-meta'], queryFn: () => get<SkillMeta>('/api/skills/meta'), staleTime: Infinity });
  const [filter, setFilter] = useState<'all' | SkillStatus>('all');
  const [sel, setSel] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const [importing, setImporting] = useState(false);
  const items = list.data?.items ?? [];
  const shown = items.filter((s) => filter === 'all' || s.status === filter);
  const current = creating ? null : (sel && items.some((s) => s.id === sel) ? sel : shown[0]?.id ?? null);
  // 分类按固定顺序排（后台设定的分类在前，其余在后）
  const order = meta.data?.categories ?? [];
  const cats = [...new Set(shown.map((s) => s.category))].sort((a, b) => (order.indexOf(a) + 1 || 99) - (order.indexOf(b) + 1 || 99));
  const count = (st: SkillStatus) => items.filter((s) => s.status === st).length;
  const used = items.reduce((a, s) => a + s.usedWeek, 0);
  const d = list.data;

  return (
    <>
      <div className="qy-tabs sk-top">
        <b>技能管理</b>
        <span className="hint">给助手的专家答题指南：碰到对应的问题，它自己翻出来照着答</span>
        <span className="sk-top-r">
          <button type="button" className="gbtn" onClick={() => setImporting(true)}><Icon name="upload" size={13} style={{ verticalAlign: '-2px', marginRight: 4 }} />导入 SKILL.md</button>
          <button type="button" className="pbtn" onClick={() => { setCreating(true); setSel(null); }}><Icon name="plus" size={13} style={{ verticalAlign: '-2px', marginRight: 4 }} />新建技能</button>
        </span>
      </div>
      <div className="qy">
        {d && (
          <div className="qy-kpis">
            <div className="qy-kpi"><span>已启用</span><b className="ok">{count('Enabled')}</b><span>共 {items.length} 个</span></div>
            <div className="qy-kpi"><span>待审核</span><b>{count('Review')}</b><span>审过、启用后助手才会用</span></div>
            <div className="qy-kpi"><span>这 7 天被翻看</span><b>{used}</b><span>{d.answersWeek ? `${Math.round((d.answersWithSkillWeek / d.answersWeek) * 100)}% 的回答用了技能` : '还没有回答'}</span></div>
            <div className="qy-kpi"><span>草稿</span><b>{count('Draft')}</b><span>正文还没写完的</span></div>
          </div>
        )}
        <div className="qy-split">
          <div className="sk-list">
            <div className="qy-filters">
              {(['all', 'Enabled', 'Review', 'Draft'] as const).map((k) => (
                <button key={k} type="button" className={'q-chip' + (filter === k ? ' on' : '')} onClick={() => setFilter(k)}>
                  {k === 'all' ? `全部 ${items.length}` : STATUS[k].label}
                </button>
              ))}
              <span className="hint" style={{ marginLeft: 'auto', fontSize: 11 }}>7 天用</span>
            </div>
            {list.isLoading && <div style={{ padding: 20 }}><Spinner text="正在载入…" /></div>}
            {list.isError && <ErrorBox message="技能列表没载入" />}
            {cats.map((c) => (
              <div key={c}>
                <div className="sk-grp"><b>{c}</b><span>{shown.filter((s) => s.category === c).length}</span></div>
                {shown.filter((s) => s.category === c).map((s) => (
                  <button key={s.id} type="button" className={'sk-row' + (s.id === current ? ' sel' : '')} onClick={() => { setCreating(false); setSel(s.id); }}>
                    <span className="sk-n">{s.name}</span>
                    <span className="sk-tag">{SOURCE[s.source] ?? s.source}</span>
                    <span className={'sk-st ' + STATUS[s.status].cls}>{STATUS[s.status].label}</span>
                    <span className="sk-u">{s.usedWeek || '—'}</span>
                  </button>
                ))}
              </div>
            ))}
            {!list.isLoading && shown.length === 0 && <div className="qy-empty">这里还没有技能。</div>}
          </div>
          {meta.data && (creating
            ? <SkillEditor key="new" id={null} meta={meta.data} onSaved={(id) => { setCreating(false); setSel(id); }} />
            : current ? <SkillEditor key={current} id={current} meta={meta.data} onSaved={(id) => setSel(id)} onDeleted={() => setSel(null)} />
              : <div className="qy-detail qy-empty">左边点一个技能看详情。</div>)}
        </div>
      </div>
      {importing && <ImportBox categories={meta.data?.categories ?? []} onClose={() => setImporting(false)}
        onDone={(id) => { setImporting(false); setCreating(false); setSel(id); }} />}
    </>
  );
}
