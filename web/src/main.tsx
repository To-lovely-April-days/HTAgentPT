import React from 'react'
import ReactDOM from 'react-dom/client'
import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import './styles/global.css'
import './styles/preview.css'
import './styles/home.css'
import './styles/admin-home.css'
import { AuthProvider, useAuth } from './lib/auth'
import { Spinner } from './components/Common'
import LoginPage from './pages/Login'
import Shell from './Shell'
import ChatPage from './pages/chat/ChatPage'
import QaPage from './pages/qa/QaPage'
import ProjectsPage from './pages/projects/ProjectsPage'
import ProjectDetailPage from './pages/projects/ProjectDetailPage'
import GeneratePage from './pages/generate/GeneratePage'
import ContractPage from './pages/generate/ContractPage'
import TicketsPage from './pages/tickets/TicketsPage'
import TranslatePage from './pages/translate/TranslatePage'
import CasesPage from './pages/cases/CasesPage'
import CaseDetailPage from './pages/cases/CaseDetailPage'
import CaseEntryPage from './pages/cases/CaseEntryPage'
import AdminLayout, { AdminHome, ADMIN_PERMS } from './pages/admin/AdminLayout'
import CorpusPage from './pages/admin/CorpusPage'
import DocPreviewPage from './pages/admin/DocPreviewPage'
import KbsPage from './pages/admin/KbsPage'
import MetaPage from './pages/admin/MetaPage'
import QualityPage from './pages/admin/quality/QualityPage'
import SkillsPage from './pages/admin/skills/SkillsPage'
import HazardDataPage from './pages/admin/HazardDataPage'
import PptTemplatesPage from './pages/admin/PptTemplatesPage'
import TemplatesAdminPage from './pages/admin/TemplatesAdminPage'
import UsersPage from './pages/admin/UsersPage'
import SettingsPage from './pages/admin/SettingsPage'
import StatsPage from './pages/admin/StatsPage'
import PricesPage from './pages/prices/PricesPage'
import ReviewPage from './pages/review/ReviewPage'
import FilesPage from './pages/files/FilesPage'
import ApprovalsPage from './pages/approvals/ApprovalsPage'
import ProfilePage from './pages/profile/ProfilePage'

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

/** 各角色的落地页：管理员进后台首页（决策 3），总部审核人进审核台，其余进问答。 */
function HomeRedirect() {
  const { profile } = useAuth()
  const has = (p: string) => !!profile?.permissions.includes(p)
  // 只管后台、不用问答的人（比如只看统计的管理层）直接进后台
  const chatless = !has('qa.internal') && !has('qa.public') && !has('case.read') && ADMIN_PERMS.some(has)
  const to = profile?.roleCode === 'admin' || chatless ? '/admin' : profile?.roleCode === 'hq_reviewer' ? '/review' : '/chat'
  return <Navigate to={to} replace />
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
        <Route index element={<HomeRedirect />} />
        <Route path="chat" element={<ChatPage />} />
        <Route path="qa" element={<QaPage />} />
        <Route path="projects" element={<ProjectsPage />} />
        <Route path="projects/:projectNo" element={<ProjectDetailPage />} />
        <Route path="generate" element={<GeneratePage />} />
        <Route path="contract" element={<ContractPage />} />
        <Route path="translate" element={<TranslatePage />} />
        <Route path="cases" element={<CasesPage />} />
        <Route path="cases/new" element={<CaseEntryPage />} />
        <Route path="cases/:caseId" element={<CaseDetailPage />} />
        <Route path="cases/:caseId/edit" element={<CaseEntryPage />} />
        <Route path="tickets" element={<TicketsPage />} />
        <Route path="review" element={<ReviewPage />} />
        <Route path="files" element={<FilesPage />} />
        <Route path="approvals" element={<ApprovalsPage />} />
        <Route path="prices" element={<PricesPage />} />
        <Route path="admin" element={<AdminLayout />}>
          <Route index element={<AdminHome />} />
          <Route path="corpus" element={<CorpusPage />} />
          <Route path="corpus/:docId/preview" element={<DocPreviewPage />} />
          <Route path="kbs" element={<KbsPage />} />
          <Route path="prices" element={<PricesPage />} />
          <Route path="meta" element={<MetaPage />} />
          <Route path="quality" element={<QualityPage />} />
          <Route path="templates" element={<TemplatesAdminPage />} />
          <Route path="skills" element={<SkillsPage />} />
          <Route path="hazard-data" element={<HazardDataPage />} />
          <Route path="ppt-templates" element={<PptTemplatesPage />} />
          <Route path="users" element={<UsersPage />} />
          <Route path="settings" element={<SettingsPage />} />
          <Route path="audit" element={<StatsPage />} />
        </Route>
        <Route path="profile" element={<ProfilePage />} />
        <Route path="*" element={<HomeRedirect />} />
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
