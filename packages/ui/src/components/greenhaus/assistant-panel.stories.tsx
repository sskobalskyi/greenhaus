import type { Meta, StoryObj } from '@storybook/react-vite'
import { useState } from 'react'
import { expect, fn, userEvent, within } from 'storybook/test'
import { AssistantPanel, type AssistantPanelProps } from './assistant-panel'

/** The panel is controlled by the app. In stories the question lives here, so typing works. */
function Controlled(props: AssistantPanelProps) {
  const [question, setQuestion] = useState(props.question)
  return <AssistantPanel {...props} question={question} onQuestionChange={setQuestion} />
}

const sources = [
  { number: 1, title: 'Basil care', text: 'Basil likes warm, bright places and soil that is kept lightly moist, never soggy.' },
  { number: 2, title: 'Watering herbs', text: 'Water when the top centimetre of soil is dry. Drooping leaves are often thirst or too much water.' },
]

const meta = {
  title: 'Greenhaus/AssistantPanel',
  component: AssistantPanel,
  tags: ['autodocs'],
  decorators: [(Story) => <div className="max-w-2xl"><Story /></div>],
  render: (args) => <Controlled {...args} />,
  args: { status: 'idle', question: '', onQuestionChange: fn(), onAsk: fn(), dataAccess: 'current', used: 3, limit: 100 },
} satisfies Meta<typeof AssistantPanel>
export default meta
type Story = StoryObj<typeof meta>

export const Idle: Story = {
  play: async ({ canvasElement }) => {
    // Nothing to send until something is typed.
    await expect(within(canvasElement).getByRole('button', { name: 'Ask' })).toBeDisabled()
  },
}

/** Type a question and send it. */
export const Typing: Story = {
  play: async ({ canvasElement, args }) => {
    const canvas = within(canvasElement)
    await userEvent.type(canvas.getByLabelText('Your question'), '  Why are the leaves drooping?  ')
    await expect(canvas.getByText('32 of 500 characters')).toBeVisible()
    await userEvent.click(canvas.getByRole('button', { name: 'Ask' }))
    await expect(args.onAsk).toHaveBeenCalledWith('Why are the leaves drooping?')
  },
}

/** The answer arrives piece by piece, and the input is locked meanwhile. */
export const Asking: Story = {
  args: { status: 'asking', question: 'Why are the leaves drooping?', answer: 'Drooping leaves are often a sign of thirst. Check the soil' },
  play: async ({ canvasElement }) => {
    await expect(within(canvasElement).getByLabelText('Your question')).toBeDisabled()
  },
}

export const WaitingForTheFirstWords: Story = {
  args: { status: 'asking', question: 'Is the soil too dry?' },
}

export const Answered: Story = {
  args: {
    status: 'answered',
    question: 'Why are the leaves drooping?',
    answer: 'Drooping leaves are often a sign of thirst. Your soil moisture is 38 percent, below the 55 to 65 you set, so water it now [2].',
    sources,
    used: 4,
  },
  play: async ({ canvasElement }) => {
    await expect(within(canvasElement).getByText('Watering herbs')).toBeVisible()
  },
}

/** The model was not used: the passages that match are shown, and the question does not count. */
export const PassagesOnly: Story = {
  args: { status: 'fallback', fallback: 'retrieval_only', question: 'How often should I water basil?', answer: '', sources },
}

export const NothingFound: Story = {
  args: { status: 'fallback', fallback: 'no_results', question: 'What is the capital of France?', answer: '' },
}

export const Error: Story = {
  args: { status: 'error', question: 'Why are the leaves drooping?', onRetry: fn() },
  play: async ({ canvasElement, args }) => {
    await userEvent.click(within(canvasElement).getByRole('button', { name: 'Try again' }))
    await expect(args.onRetry).toHaveBeenCalled()
  },
}

export const Unavailable: Story = {
  args: { status: 'unavailable', question: 'Why are the leaves drooping?', onRetry: fn() },
}

/** The month's questions are used up: the input is locked and the plan is named. */
export const LimitReached: Story = {
  args: { status: 'limit', dataAccess: 'none', used: 10, limit: 10, question: '' },
  play: async ({ canvasElement }) => {
    const canvas = within(canvasElement)
    await expect(canvas.getByLabelText('Your question')).toBeDisabled()
    await expect(canvas.getByText(/used all 10 questions/)).toBeVisible()
  },
}

export const NoReadingsInThePlan: Story = {
  args: { dataAccess: 'none', used: 7, limit: 10 },
}

export const SeesHistory: Story = {
  args: { dataAccess: 'history', used: 120, limit: 500 },
}

export const AlmostUsedUp: Story = {
  args: { dataAccess: 'none', used: 9, limit: 10 },
}

export const UkrainianDark: Story = {
  globals: { locale: 'uk', theme: 'dark' },
  args: {
    status: 'answered',
    question: 'Чому в’януть листки?',
    answer: 'В’янення листків часто означає спрагу. Вологість ґрунту 38 відсотків, нижче заданих 55–65, тож полийте [2].',
    sources: [{ number: 2, title: 'Поливання трав', text: 'Поливайте, коли верхній сантиметр ґрунту сухий.' }],
    dataAccess: 'current',
  },
}
