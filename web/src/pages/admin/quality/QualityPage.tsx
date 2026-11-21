// 后台「回答质量」：答错的问题 + 测试题与准确率。答错了先看错在哪一步、当场修；修完存成测试题，以后每次调整都跑一遍。
import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { get } from '../../../lib/api';
import { IssuesTab } from './IssuesTab';
import { EvalTab } from './EvalTab';
import type { QualitySummary } from './types';
import '../../../styles/quality.css';

export default function QualityPage() {
  const [tab, setTab] = useState<'issues' | 'eval'>('issues');
  const s = useQuery({ queryKey: ['quality-summary'], queryFn: () => get<QualitySummary>('/api/quality/summary?days=7') });
  return (
    <>
      <div className="qy-tabs" role="tablist">
        <b>回答质量</b>
        <button type="button" role="tab" aria-selected={tab === 'issues'} className={tab === 'issues' ? 'on' : ''} onClick={() => setTab('issues')}>
          答错的问题{s.data && s.data.open > 0 && <span className="qy-count">{s.data.open}</span>}
        </button>
        <button type="button" role="tab" aria-selected={tab === 'eval'} className={tab === 'eval' ? 'on' : ''} onClick={() => setTab('eval')}>测试题与准确率</button>
      </div>
      {tab === 'issues' ? <IssuesTab /> : <EvalTab />}
    </>
  );
}
