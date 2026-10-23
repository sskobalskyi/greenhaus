// Captures key stories from the built Storybook in light/English and dark/Ukrainian, for the README.
//   npm run build-storybook && npm run screenshots
// With a name, only that story is captured:  npm run screenshots -- assistant-answered
import { createReadStream, existsSync, mkdirSync, statSync } from 'node:fs'
import { createServer } from 'node:http'
import path from 'node:path'
import { chromium } from 'playwright'

const root = path.resolve(import.meta.dirname, '..', 'storybook-static')
const out = path.resolve(import.meta.dirname, '..', '..', '..', 'docs', 'screenshots', 'ui')
mkdirSync(out, { recursive: true })

const types = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.png': 'image/png' }
const server = createServer((request, response) => {
  const url = new URL(request.url ?? '/', 'http://localhost')
  let file = path.join(root, decodeURIComponent(url.pathname))
  if (!file.startsWith(root) || !existsSync(file) || statSync(file).isDirectory()) file = path.join(root, 'index.html')
  response.setHeader('content-type', types[path.extname(file)] ?? 'application/octet-stream')
  createReadStream(file).pipe(response)
}).listen(0)

const port = server.address().port
const stories = [
  ['pot-card-attention', 'greenhaus-potcard--needs-attention-in-one-zone', 440],
  // Needs the mock backend: docker compose up mock
  ['pots-from-mock', 'greenhaus-potcard--from-mock', 1240],
  ['pot-zone-map', 'greenhaus-potzonemap--dry-zone', 640],
  ['actuator-control-locked', 'greenhaus-actuatorcontrol--auto-locked-by-tier', 480],
  ['tier-cards', 'greenhaus-tiercard--side-by-side', 1200],
  ['requirement-bands', 'greenhaus-requirementband--all-metrics', 480],
  ['care-task', 'greenhaus-caretaskitem--from-ph-sensor', 520],
  ['assistant-answered', 'greenhaus-assistantpanel--answered', 760],
]
const looks = [
  ['light-en', 'locale:en;theme:light'],
  ['dark-uk', 'locale:uk;theme:dark'],
]

const browser = await chromium.launch()
const page = await browser.newPage({ deviceScaleFactor: 2 })
for (const [name, id, width] of stories) {
  if (process.argv[2] && process.argv[2] !== name) continue
  for (const [look, globals] of looks) {
    await page.setViewportSize({ width, height: 800 })
    await page.goto(`http://localhost:${port}/iframe.html?id=${id}&viewMode=story&globals=${globals}`)
    await page.waitForSelector('#storybook-root > *')
    await page.evaluate(() => document.fonts.ready)
    await page.waitForTimeout(300)
    await page.locator('#storybook-root').screenshot({ path: path.join(out, `${name}-${look}.png`) })
    console.log(`${name}-${look}.png`)
  }
}
await browser.close()
server.close()
