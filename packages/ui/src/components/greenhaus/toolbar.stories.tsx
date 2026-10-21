import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, userEvent, within } from 'storybook/test'
import { LanguageSwitcher } from './language-switcher'
import { ThemeToggle } from './theme-toggle'

const meta = { title: 'Greenhaus/Toolbar', tags: ['autodocs'] } satisfies Meta
export default meta
type Story = StoryObj<typeof meta>

export const LanguageAndTheme: Story = {
  render: () => (
    <div className="flex items-center gap-3">
      <LanguageSwitcher />
      <ThemeToggle />
    </div>
  ),
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await userEvent.click(canvas.getByRole('radio', { name: 'Українська' }))
    await expect(canvas.getByRole('radio', { name: 'Українська' })).toBeChecked()
    await userEvent.click(canvas.getByRole('radio', { name: 'English' }))
  },
}
