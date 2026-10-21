import { useTranslation } from 'react-i18next'
import { ToggleGroup, ToggleGroupItem } from '@/components/ui/toggle-group'
import { locales, type Locale } from '@/i18n'

const names: Record<Locale, string> = { en: 'EN', uk: 'УК' }

export function LanguageSwitcher() {
  const { t, i18n } = useTranslation()
  return (
    <ToggleGroup
      type="single"
      size="sm"
      variant="outline"
      value={i18n.language}
      onValueChange={(value) => value && void i18n.changeLanguage(value)}
      aria-label={t('common.language')}
    >
      {locales.map((locale) => (
        <ToggleGroupItem key={locale} value={locale} aria-label={locale === 'en' ? 'English' : 'Українська'}>
          {names[locale]}
        </ToggleGroupItem>
      ))}
    </ToggleGroup>
  )
}
