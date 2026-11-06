# Browser checks (Playwright + installed Chrome)

Run with the local stack up (API on :5080, npm run dev on :3000); PLAYWRIGHT_PATH points at any Playwright install.

    PLAYWRIGHT_PATH=<path>/node_modules/playwright node web/scripts/e2e/all-modules.js LOGINS.local.txt
    HEADLESS=1 PLAYWRIGHT_PATH=... node web/scripts/e2e/responsive.js LOGINS.local.txt
    HEADLESS=1 PLAYWRIGHT_PATH=... node web/scripts/e2e/validation.js LOGINS.local.txt

- all-modules: every page and tab as platform operator, factory admin and demo viewer, plus ISO 50001 / carbon flows.
- responsive: horizontal overflow at 390, 768 and 1366 px on every page and tab.
- validation: every add form submitted empty must be refused clearly.
