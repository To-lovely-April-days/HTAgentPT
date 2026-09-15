import React from 'react'
import ReactDOM from 'react-dom/client'
import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import './styles/global.css'
import { AuthProvider, useAuth } from './lib/auth'
import { Spinner } from './components/Common'
import LoginPage from './pages/Login'
import Shell from './Shell'
import QaPage from './pages/qa/QaPage'
import ProjectsPage from './pages/projects/ProjectsPage'
import ProjectDetailPage from './pages/projects/ProjectDetailPage'
import CasesPage from './pages/cases/CasesPage'
import CaseDetailPage from './pages/cases/CaseDetailPage'
import CaseEntryPage from './pages/cases/CaseEntryPage'
import Placeholder from './pages/Placeholder'

const queryClient = new QueryClient({
  defaultOptions: {
    queries: { retry: 1, refetchOnWindowFocus: false, staleTime: 30_000 },
  },
})

function Guarded({ children }: { children: React.ReactNode }) {
  const { profile, booting } = useAuth()
  if (booting) {
    return (
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', height: '100vh' }}>
        <Spinner text="正在进入工作台…" />
      </div>
    )
  }
  if (!profile) return <Navigate to="/login" replace />
  return <>{children}</>
}

function App() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route
        path="/"
        element={
          <Guarded>
            <Shell />
          </Guarded>
        }
      >
        <Route index element={<Navigate to="/qa" replace />} />
        <Route path="qa" element={<QaPage />} />
        <Route path="projects" element={<ProjectsPage />} />
        <Route path="projects/:projectNo" element={<ProjectDetailPage />} />
        <Route path="generate" element={<Placeholder name="方案生成" api="/api/generate/*" />} />
        <Route path="contract" element={<Placeholder name="报价与合同" api="/api/contract/*, /api/clauses" />} />
        <Route path="translate" element={<Placeholder name="资料翻译" api="/api/translate/*" />} />
        <Route path="cases" element={<CasesPage />} />
        <Route path="cases/new" element={<CaseEntryPage />} />
        <Route path="cases/:caseId" element={<CaseDetailPage />} />
        <Route path="cases/:caseId/edit" element={<CaseEntryPage />} />
        <Route path="tickets" element={<Placeholder name="报修工单" api="/api/tickets" />} />
        <Route path="review" element={<Placeholder name="共享案例审核台" api="/api/review/*" />} />
        <Route path="admin" element={<Placeholder name="管理后台" api="/api/users, /api/kbs, /api/config, /api/audit, /api/ops/*" />} />
        <Route path="profile" element={<Placeholder name="个人中心" api="/api/profile" />} />
        <Route path="*" element={<Navigate to="/qa" replace />} />
      </Route>
    </Routes>
  )
}

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <AuthProvider>
          <App />
        </AuthProvider>
      </BrowserRouter>
    </QueryClientProvider>
  </React.StrictMode>,
)
