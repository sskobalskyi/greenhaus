import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, userEvent, within } from 'storybook/test'
import { CalibrationForm } from './calibration-form'

const meta = {
  title: 'Greenhaus/CalibrationForm',
  component: CalibrationForm,
  tags: ['autodocs'],
  decorators: [(Story) => <div className="max-w-xl"><Story /></div>],
  args: { sensorCount: 1, onSubmit: fn() },
} satisfies Meta<typeof CalibrationForm>
export default meta
type Story = StoryObj<typeof meta>

export const SaveTwoValues: Story = {
  play: async ({ canvasElement, args }) => {
    const canvas = within(canvasElement)
    await expect(canvas.getByRole('button', { name: 'Save calibration' })).toBeDisabled()
    await userEvent.type(canvas.getByLabelText('Raw value with the sensor dry, in the air'), '3100')
    await userEvent.type(canvas.getByLabelText('Raw value with the sensor wet, in water'), '1300')
    await userEvent.click(canvas.getByRole('button', { name: 'Save calibration' }))
    await expect(args.onSubmit).toHaveBeenCalledWith({ dryRaw: 3100, wetRaw: 1300, applyToAll: true })
  },
}

/** Two equal values would make the scale meaningless, so they are refused before anything is sent. */
export const EqualValuesAreRefused: Story = {
  play: async ({ canvasElement, args }) => {
    const canvas = within(canvasElement)
    await userEvent.type(canvas.getByLabelText('Raw value with the sensor dry, in the air'), '2000')
    await userEvent.type(canvas.getByLabelText('Raw value with the sensor wet, in water'), '2000')
    await expect(canvas.getByRole('alert')).toHaveTextContent('must differ')
    await expect(canvas.getByRole('button', { name: 'Save calibration' })).toBeDisabled()
    await expect(args.onSubmit).not.toHaveBeenCalled()
  },
}

/** A bed with four sensors can reuse one pair of values for all of them. */
export const SeveralSensors: Story = {
  args: { sensorCount: 4 },
  play: async ({ canvasElement }) => {
    await expect(within(canvasElement).getByLabelText(/all 4 soil moisture sensors/)).toBeChecked()
  },
}

export const UkrainianDark: Story = {
  globals: { locale: 'uk', theme: 'dark' },
  args: { sensorCount: 4 },
}