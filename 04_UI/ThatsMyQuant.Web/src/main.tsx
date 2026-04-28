import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { ThemeProvider } from 'next-themes'
import './index.css'
import App from './App.tsx'
import { installNotificationSideEffects } from './lib/notificationPrefs'

// Wires sound + desktop notifications to the global notification bus
installNotificationSideEffects()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <ThemeProvider
      attribute="class"
      defaultTheme="dark"
      themes={["light", "dark", "noir"]}
      // Each theme name → single CSS class.  next-themes calls
      // `classList.add(value)` which THROWS on space-separated strings, so
      // each value here must be a single token. Tailwind's darkMode selector
      // is configured to treat both `.dark` AND `.noir` as dark-mode active,
      // and `index.css` layers extra pure-black overrides under `html.noir`.
      value={{ light: "light", dark: "dark", noir: "noir" }}
      enableSystem={false}
    >
      <App />
    </ThemeProvider>
  </StrictMode>,
)
