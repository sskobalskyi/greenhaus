import {
  Droplets,
  FlaskConical,
  Sprout,
  Sun,
  Thermometer,
  Wind,
  type LucideIcon,
} from 'lucide-react'
import type { Metric } from '@/api/client'

export const metrics: Metric[] = ['temperature', 'air_humidity', 'soil_moisture', 'ph', 'light', 'airflow']

export const metricIcon: Record<Metric, LucideIcon> = {
  temperature: Thermometer,
  air_humidity: Droplets,
  soil_moisture: Sprout,
  ph: FlaskConical,
  light: Sun,
  airflow: Wind,
}

// Full class names so Tailwind can see them.
export const metricText: Record<Metric, string> = {
  temperature: 'text-metric-temperature',
  air_humidity: 'text-metric-air-humidity',
  soil_moisture: 'text-metric-soil-moisture',
  ph: 'text-metric-ph',
  light: 'text-metric-light',
  airflow: 'text-metric-airflow',
}

export const metricBand: Record<Metric, string> = {
  temperature: 'bg-metric-temperature/25',
  air_humidity: 'bg-metric-air-humidity/25',
  soil_moisture: 'bg-metric-soil-moisture/25',
  ph: 'bg-metric-ph/25',
  light: 'bg-metric-light/30',
  airflow: 'bg-metric-airflow/25',
}

export const defaultUnit: Record<Metric, string> = {
  temperature: '°C',
  air_humidity: '%',
  soil_moisture: '%',
  ph: 'pH',
  light: 'lux',
  airflow: '',
}

/** A unit as people write it: devices send plain `C`, people expect the degree sign. */
export function displayUnit(unit: string): string {
  return unit === 'C' ? '\u00B0C' : unit
}

/** Formats a reading for display: whole lux, one decimal elsewhere, with the locale's separators. */
export function formatValue(metric: Metric, value: number, locale: string): string {
  return new Intl.NumberFormat(locale, { maximumFractionDigits: metric === 'light' ? 0 : 1 }).format(value)
}
