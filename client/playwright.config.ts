import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  workers: 1,
  timeout: 45000,
  use: {
    baseURL: process.env.FIRESIDE_BASE_URL ?? 'http://127.0.0.1:5180',
    extraHTTPHeaders: { Origin: new URL(process.env.FIRESIDE_BASE_URL ?? 'http://127.0.0.1:5180').origin },
    trace: 'retain-on-failure',
  },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'] } },
    { name: 'mobile', use: { ...devices['iPhone 13'], defaultBrowserType: 'chromium' } },
  ],
});
