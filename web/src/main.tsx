import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import App from './App'
import './styles.css'

const el = document.getElementById('root')
if (!el) {
  // index.html 被改坏时也要给出可读提示，而不是静默空白页。
  document.body.innerHTML = '<pre style="padding:24px;color:#b00">启动失败：找不到 #root 节点</pre>'
} else {
  createRoot(el).render(
    <StrictMode>
      <App />
    </StrictMode>,
  )
}
