/** Waits until Keycloak has imported the realm and the API answers, so the tests never race the stack. */
const targets = [
  'http://localhost:8181/realms/greenhaus/.well-known/openid-configuration',
  'http://localhost:8091/health',
  'http://localhost:8090/api/billing/v1/plans',
]

export default async function waitForStack() {
  const deadline = Date.now() + 180_000
  for (const url of targets) {
    for (;;) {
      try {
        if ((await fetch(url)).ok) break
      } catch {
        /* not up yet */
      }
      if (Date.now() > deadline) throw new Error(`${url} did not become ready. Is the stack running? See the README.`)
      await new Promise((resolve) => setTimeout(resolve, 1000))
    }
  }
}