import { i18n } from '@greenhaus/ui'
import en from './en.json'
import uk from './uk.json'

// The design system owns the i18next instance. The app adds its own strings to it.
i18n.addResourceBundle('en', 'translation', en, true, true)
i18n.addResourceBundle('uk', 'translation', uk, true, true)

// Remember the language and keep the document language in step with it.
const stored = (() => {
  try {
    return localStorage.getItem('locale')
  } catch {
    return null
  }
})()
const preferred = stored ?? (navigator.language.startsWith('uk') ? 'uk' : 'en')
void i18n.changeLanguage(preferred)

i18n.on('languageChanged', (language) => {
  document.documentElement.lang = language
  try {
    localStorage.setItem('locale', language)
  } catch {
    /* storage may be unavailable */
  }
})
document.documentElement.lang = preferred
