// 输入框左下角的「＋」：传文件、插件（工具开关 + 技能）、资料范围、放进项目都收在这里。
// 二级列表就地换掉菜单内容（上面一条「‹ 返回」），窄屏也放得下。选过的技能、资料范围在「＋」旁边以小标签出现（见 Chips）。
import { useEffect, useMemo, useRef, useState } from 'react';
import { Icon } from '../../components/Icons';
import { projectColor, scopeLabel } from './composerUtil';
import type { PluginRow, ProjectRow, Scope, ScopeOptions, SkillRow } from './composerUtil';

export interface PlusProps {
  onUpload?: () => void;
  skills?: { list: SkillRow[]; value: SkillRow | null; onChange: (s: SkillRow | null) => void };
  /** 工具开关：关掉的这个人以后提问都不给助手用（记在这台电脑上） */
  plugins?: { list: PluginRow[]; off: string[]; onToggle: (code: string) => void };
  /** 管理员：去后台管工具和技能 */
  onManage?: () => void;
  /** 记忆：助手记住这个人的偏好、在跟的事，首页按它推荐；默认开着，这里一点就关 */
  memory?: { on: boolean; onToggle: () => void };
  scope?: { value: Scope; options: ScopeOptions; onChange: (s: Scope) => void };
  project?: { list: ProjectRow[]; value: string | null; onChange: (id: string | null) => void; onNew: () => void };
  /** PPT 模板：点了打开选择窗（网格看封面，菜单里放不下） */
  ppt?: { value: string | null; onOpen: () => void };
}

type View = 'main' | 'plugin' | 'scope' | 'project';

export function PlusMenu({ onUpload, skills, plugins, onManage, memory, scope, project, ppt }: PlusProps) {
  const [open, setOpen] = useState(false);
  const [view, setView] = useState<View>('main');
  const [q, setQ] = useState('');
  const box = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const down = (e: MouseEvent) => { if (!box.current?.contains(e.target as Node)) setOpen(false); };
    const key = (e: KeyboardEvent) => { if (e.key === 'Escape') { e.stopPropagation(); setOpen(false); } };
    document.addEventListener('mousedown', down);
    document.addEventListener('keydown', key);
    return () => { document.removeEventListener('mousedown', down); document.removeEventListener('keydown', key); };
  }, [open]);
  const toggle = () => { setOpen(!open); setView('main'); setQ(''); };
  const done = () => setOpen(false);

  const k = q.trim().toLowerCase();
  const skillRows = useMemo(() => (skills?.list ?? []).filter((s) => !k || s.name.toLowerCase().includes(k) || s.description.toLowerCase().includes(k) || s.category.toLowerCase().includes(k)), [skills?.list, k]);
  const toolRows = useMemo(() => (plugins?.list ?? []).filter((p) => !k || p.label.toLowerCase().includes(k) || p.summary.toLowerCase().includes(k)), [plugins?.list, k]);
  const hasPlugin = !!skills?.list.length || !!plugins?.list.length;
  const offCount = plugins ? plugins.list.filter((p) => plugins.off.includes(p.code)).length : 0;

  if (!onUpload && !hasPlugin && !scope && !project && !ppt && !memory) return null;
  const cur = project?.list.find((p) => p.id === project.value);
  const back = (title: string) => (
    <button type="button" className="pm-back" onClick={() => setView('main')}><Icon name="left" size={14} />{title}</button>
  );

  return (
    <div className="pm" ref={box}>
      <button type="button" className={'pm-btn' + (open ? ' on' : '')} aria-haspopup="menu" aria-expanded={open}
        aria-label="更多：传文件、插件、资料范围、放进项目" title="传文件、插件、资料范围、放进项目" onClick={toggle}>
        <Icon name="plus" size={17} stroke={2} />
      </button>
      {open && (
        <div className={'pm-pop' + (view === 'plugin' ? ' wide' : '')} role="menu">
          {view === 'main' && (
            <>
              {onUpload && (
                <button type="button" role="menuitem" className="pm-i" onClick={() => { done(); onUpload(); }}>
                  <Icon name="clip" size={15} /><span className="pm-t">上传文件</span><span className="pm-k">Word Excel PPT PDF</span>
                </button>
              )}
              {hasPlugin && (
                <button type="button" role="menuitem" className="pm-i" onClick={() => setView('plugin')}>
                  <Icon name="plug" size={15} />
                  <span className="pm-t">插件{(skills?.value || offCount > 0) && <small>{[skills?.value?.name, offCount > 0 ? `关了 ${offCount} 个` : null].filter(Boolean).join(' · ')}</small>}</span>
                  <Icon name="right" size={13} className="pm-r" />
                </button>
              )}
              {ppt && (
                <button type="button" role="menuitem" className="pm-i" onClick={() => { done(); ppt.onOpen(); }}>
                  <Icon name="slides" size={15} /><span className="pm-t">PPT 模板<small>{ppt.value ?? '助手按场景挑'}</small></span><Icon name="right" size={13} className="pm-r" />
                </button>
              )}
              {scope && (
                <>
                  <div className="pm-sep" />
                  <button type="button" role="menuitem" className="pm-i" onClick={() => setView('scope')}>
                    <Icon name="folder" size={15} /><span className="pm-t">资料范围<small>{scopeLabel(scope.value, scope.options) ?? '全部资料'}</small></span><Icon name="right" size={13} className="pm-r" />
                  </button>
                </>
              )}
              {project && (
                <>
                  <div className="pm-sep" />
                  <button type="button" role="menuitem" className="pm-i" onClick={() => setView('project')}>
                    <Icon name="box" size={15} /><span className="pm-t">放进项目<small>{cur ? cur.name : '现在不在项目里'}</small></span><Icon name="right" size={13} className="pm-r" />
                  </button>
                </>
              )}
              {memory && (
                <>
                  <div className="pm-sep" />
                  <button type="button" role="menuitemcheckbox" aria-checked={memory.on} className="pm-i" onClick={memory.onToggle}
                    title="助手记住你的偏好和在跟的事，回答和首页推荐都会参考；在「个人中心」里能看、能改、能删">
                    <Icon name="brain" size={15} /><span className="pm-t">记忆<small>{memory.on ? '开着 · 首页按它推荐' : '关着'}</small></span>
                    {memory.on && <Icon name="check" size={15} stroke={2.2} className="pm-r pm-on" />}
                  </button>
                </>
              )}
            </>
          )}

          {view === 'plugin' && (
            <>
              {back('插件')}
              <label className="pm-search"><Icon name="search" size={13} />
                <input autoFocus value={q} onChange={(e) => setQ(e.target.value)} placeholder="搜插件" aria-label="搜插件" />
              </label>
              <div className="pm-list sc">
                {toolRows.map((p) => {
                  const on = !plugins!.off.includes(p.code);
                  return (
                    <button key={p.code} type="button" role="menuitemcheckbox" aria-checked={on} className="pm-i pm-tool" title={p.summary}
                      onClick={() => plugins!.onToggle(p.code)}>
                      <span className="pm-t">{p.label}</span>
                      <span className={'pm-sw' + (on ? ' on' : '')} aria-hidden="true"><i /></span>
                    </button>
                  );
                })}
                {toolRows.length > 0 && skillRows.length > 0 && <div className="pm-sep" />}
                {skillRows.map((s) => (
                  <button key={s.code} type="button" role="menuitemradio" aria-checked={skills!.value?.code === s.code} className="pm-i" title={s.description}
                    onClick={() => { skills!.onChange(skills!.value?.code === s.code ? null : s); done(); }}>
                    <Icon name="book" size={15} /><span className="pm-t">{s.name}</span>
                    {skills!.value?.code === s.code && <Icon name="check" size={14} stroke={2.4} className="pm-r" />}
                  </button>
                ))}
                {toolRows.length === 0 && skillRows.length === 0 && <div className="pm-g">没有对得上的插件</div>}
              </div>
              {onManage && (
                <>
                  <div className="pm-sep" />
                  <button type="button" role="menuitem" className="pm-i" onClick={() => { done(); onManage(); }}>
                    <Icon name="settings" size={15} /><span className="pm-t">管理插件</span><Icon name="right" size={13} className="pm-r" />
                  </button>
                </>
              )}
            </>
          )}

          {view === 'scope' && scope && (
            <>
              {back('资料范围')}
              <div className="pm-list sc">
                {([['all', '全部资料', null]] as [string, string, string | null][]).concat(
                  scope.options.kbs.map((k) => [`kb:${k.id}`, k.name, '知识库']),
                  scope.options.cats.map((c) => [`cat:${c}`, c, '文档类别']),
                ).map(([v, label, grp], i, arr) => {
                  const now = scope.value.kbId ? `kb:${scope.value.kbId}` : scope.value.docCategory ? `cat:${scope.value.docCategory}` : 'all';
                  const head = grp && arr[i - 1]?.[2] !== grp;
                  return (
                    <div key={v}>
                      {head && <div className="pm-g">{grp}</div>}
                      <button type="button" role="menuitemradio" aria-checked={now === v} className="pm-i"
                        onClick={() => {
                          scope.onChange(v.startsWith('kb:') ? { kbId: v.slice(3), docCategory: null }
                            : v.startsWith('cat:') ? { kbId: null, docCategory: v.slice(4) } : { kbId: null, docCategory: null });
                          done();
                        }}>
                        <span className="pm-t">{label}</span>{now === v && <Icon name="check" size={14} stroke={2.4} className="pm-r" />}
                      </button>
                    </div>
                  );
                })}
              </div>
              <div className="pm-foot">只在选定的资料里找答案；管这一整个对话。</div>
            </>
          )}

          {view === 'project' && project && (
            <>
              {back('放进项目')}
              <div className="pm-list sc">
                {project.list.length === 0 && <div className="pm-g">还没有项目</div>}
                {project.list.map((p) => (
                  <button key={p.id} type="button" role="menuitemradio" aria-checked={project.value === p.id} className="pm-i"
                    onClick={() => { if (project.value !== p.id) project.onChange(p.id); done(); }}>
                    <span className="pj-ic" style={{ background: projectColor(p.id) }}>{p.name.slice(0, 1)}</span>
                    <span className="pm-t">{p.name}{p.ledgerNo && <small>台账 {p.ledgerNo}</small>}</span>
                    {project.value === p.id && <Icon name="check" size={14} stroke={2.4} className="pm-r" />}
                  </button>
                ))}
                <div className="pm-sep" />
                {project.value && (
                  <button type="button" role="menuitem" className="pm-i" onClick={() => { project.onChange(null); done(); }}>
                    <Icon name="x" size={14} /><span className="pm-t">移出项目</span>
                  </button>
                )}
                <button type="button" role="menuitem" className="pm-i" onClick={() => { done(); project.onNew(); }}>
                  <Icon name="plus" size={14} /><span className="pm-t">新建项目</span>
                </button>
              </div>
            </>
          )}
        </div>
      )}
    </div>
  );
}
