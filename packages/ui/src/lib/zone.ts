import type { TFunction } from 'i18next'
import type { Schemas } from '@/api/client'

/**
 * The name to show for a zone. The API only returns a label the user chose, so the default is built here
 * in the language of the interface: numbered by row and column in a grid, or "Whole pot" in the Simple view.
 */
export function zoneName(t: TFunction, zone: Schemas['Zone'], grid: { simple: boolean; cols: number }): string {
  if (zone.label) return zone.label
  return grid.simple ? t('zone.whole') : t('zone.default', { n: zone.row * grid.cols + zone.col + 1 })
}