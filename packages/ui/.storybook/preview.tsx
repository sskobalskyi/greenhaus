import { withThemeByClassName } from '@storybook/addon-themes'
import type { Preview } from '@storybook/react-vite'
import { useLayoutEffect, type ReactNode } from 'react'
import i18n from '../src/i18n'
import '../src/index.css'
import { TooltipProvider } from '../src/components/ui/tooltip'

function Providers({ locale, children }: { locale: string; children: ReactNode }) {
  // Switch before paint so a story never flashes in the wrong language.
  useLayoutEffect(() => {
    if (i18n.language !== locale) void i18n.changeLanguage(locale)
  }, [locale])
  return (
    <TooltipProvider>
      <div className="bg-background p-4 text-foreground">{children}</div>
    </TooltipProvider>
  )
}

const preview: Preview = {
  initialGlobals: { locale: 'en' },
  globalTypes: {
    locale: {
      description: 'Language of the interface',
      toolbar: {
        title: 'Language',
        icon: 'globe',
        dynamicTitle: true,
        items: [
          { value: 'en', title: 'English' },
          { value: 'uk', title: 'Українська' },
        ],
      },
    },
  },
  decorators: [
    withThemeByClassName({ themes: { light: '', dark: 'dark' }, defaultTheme: 'light' }),
    (Story, context) => (
      <Providers locale={String(context.globals.locale ?? 'en')}>
        <Story />
      </Providers>
    ),
  ],
  parameters: {
    layout: 'fullscreen',
    controls: { matchers: { color: /(background|color)$/i, date: /Date$/i } },
    // Accessibility violations fail the story tests.
    a11y: { test: 'error' },
  },
}

export default preview
