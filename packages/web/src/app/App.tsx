import { BrowserRouter } from 'react-router-dom'
import { ThemeProvider } from '../shared/lib/theme/ThemeProvider'
import { ProjectProvider } from '../entities/project'
import { AuthGate } from './AuthGate'

export { AppContent } from './AppContent'

export default function App() {
  return (
    <ThemeProvider>
      <ProjectProvider>
        <BrowserRouter>
          <AuthGate />
        </BrowserRouter>
      </ProjectProvider>
    </ThemeProvider>
  )
}
