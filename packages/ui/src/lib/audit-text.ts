import type { TFunction } from 'i18next'

/** The metric names the server writes in its English audit reasons, and the metric each stands for. */
const metricOf: Record<string, string> = {
  temperature: 'temperature',
  'air humidity': 'air_humidity',
  'soil moisture': 'soil_moisture',
  ph: 'ph',
  // The tier fallback writes the metric as the enum name in lower case.
  soilmoisture: 'soil_moisture',
  airhumidity: 'air_humidity',
  light: 'light',
  airflow: 'airflow',
}

const actionOf: Record<string, string> = {
  watering: 'watering',
  heating: 'heating',
  'light on': 'light_on',
  'fan on': 'fan_on',
  humidifying: 'humidifying',
}

const MODE = '(off|notify|auto)'
const NUMBER = '(-?[\\d.]+)'

/**
 * The audit log keeps the reason as one English sentence, written by the server. This turns the sentences the server is
 * known to write into the person's language, and leaves any other text as it came. The sentences are listed in
 * `Greenhaus.Domain/Automation.cs`, `Greenhaus.Api/Automation/TierClamp.cs` and `Greenhaus.Api/Features/ControlEndpoints.cs`;
 * the better fix is a reason code with parameters in the contract, and this keeps old entries readable when it comes.
 */
export function auditText(t: TFunction, locale: string, reason: string): string {
  const number = new Intl.NumberFormat(locale, { maximumFractionDigits: 1 })
  const num = (value: string) => number.format(Number(value))
  const metric = (name: string) => {
    const key = metricOf[name.toLowerCase()]
    return key ? t(`metric.${key}`) : name
  }
  const device = (kind: string) => t(`actuator.kind.${kind}`, { defaultValue: kind }).toLocaleLowerCase(locale)
  const mode = (value: string) => t(`mode.${value}`)
  const relation = (value: string) => t(`audit.relation.${value}`)
  let m: RegExpMatchArray | null

  if ((m = reason.match(/^(\d+) s requested by the user$/))) return t('audit.runRequested', { seconds: m[1] })
  if ((m = reason.match(new RegExp(`^${MODE} -> ${MODE}$`)))) return t('audit.modeChanged', { from: mode(m[1]), to: mode(m[2]) })
  if ((m = reason.match(new RegExp(`^${MODE} -> ${MODE}: the (\\w+) plan does not include automatic (.+) control$`))))
    return t('audit.modeClamped', { from: mode(m[1]), to: mode(m[2]), tier: t(`tier.name.${m[3]}`, { defaultValue: m[3] }), metric: metric(m[4].replace('_', ' ')) })
  if ((m = reason.match(/^Controller (\S+) linked$/))) return t('audit.deviceLinked', { id: m[1] })
  if ((m = reason.match(/^Controller (\S+) was retired$/))) return t('audit.deviceRetired', { id: m[1] })
  if (reason === 'Safety limits tightened') return t('audit.limitsTightened')
  if (reason === 'All automation stopped by the user') return t('audit.killOn')
  if (reason === 'Automation resumed by the user') return t('audit.killOff')
  if ((m = reason.match(/^(.+) is back in range, stopped$/))) return t('audit.backInRange', { metric: metric(m[1]) })
  if ((m = reason.match(new RegExp(`^(.+) is ${NUMBER}, (below|above) the target ${NUMBER}: (.+) for (\\d+) s$`))) && actionOf[m[5]])
    return t('audit.acting', { metric: metric(m[1]), value: num(m[2]), relation: relation(m[3]), target: num(m[4]), action: t(`audit.action.${actionOf[m[5]]}`), seconds: m[6] })
  if ((m = reason.match(new RegExp(`^(.+) is ${NUMBER}, (below|above) the target ${NUMBER}\\. Run the (\\w+) to fix it$`))))
    return t('audit.suggest', { metric: metric(m[1]), value: num(m[2]), relation: relation(m[3]), target: num(m[4]), device: device(m[5]) })
  if ((m = reason.match(/^No recent (.+) reading, so nothing was done$/))) return t('audit.sensorStale', { metric: metric(m[1]) })
  if (reason === 'A heater does not run automatically without a declared hardware thermal cutoff') return t('audit.thermalCutoff')
  if (reason === 'The soil moisture sensor is not calibrated, so the pump does not run automatically') return t('audit.calibration')
  if (reason === 'The water tank is empty, so the pump does not run') return t('audit.tankEmpty')
  if ((m = reason.match(/^The daily limit of (\d+) s for the (\w+) is used up$/))) return t('audit.dailyCap', { seconds: m[1], device: device(m[2]) })
  if ((m = reason.match(/^(\d+) runs in a row did not change the (.+)\. The (\w+) is stopped, check it \(an empty tank or a broken part\)$/)))
    return t('audit.fruitless', { count: Number(m[1]), metric: metric(m[2]), device: device(m[3]) })
  return reason
}
