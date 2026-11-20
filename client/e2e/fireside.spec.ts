import { test, expect } from '@playwright/test';

test('named audience and separate host share live moderated content and recover a connection', async ({ browser, page }) => {
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.goto('/');
  await expect(page.getByRole('heading', { name: /a little warmth/i })).toBeVisible();
  await page.getByLabel('What should we call you?').fill('Browser participant');
  await page.getByRole('button', { name: 'Pull up a chair' }).click();
  await expect(page.getByText('Connected to the fireside')).toBeVisible();
  const firstVote = page.getByRole('button', { name: /^Upvote:/ }).first();
  const firstQuestion = (await firstVote.getAttribute('aria-label'))!.replace('Upvote: ', '');
  await firstVote.click();
  await expect(page.getByRole('button', { name: `Voted for: ${firstQuestion}`, exact: true })).toBeDisabled();
  await page.reload();
  await expect(page.getByRole('button', { name: `Voted for: ${firstQuestion}`, exact: true })).toBeDisabled();

  const hostContext = await browser.newContext();
  const host = await hostContext.newPage();
  await host.goto('/admin');
  await host.getByRole('button', { name: 'Enter demo host desk' }).click();
  await expect(host.getByRole('heading', { name: 'Keep the conversation glowing.' })).toBeVisible();
  await host.getByRole('button', { name: 'Add question', exact: true }).click();
  const questionText = `A browser-tested Christmas question ${Date.now()}`;
  const editor = host.locator('.editor-form');
  await editor.getByLabel('Question', { exact: true }).fill(questionText);
  await editor.getByLabel('Category').fill('Sample browser test');
  await editor.getByRole('button', { name: 'Save changes' }).click();
  await expect(host.getByRole('status').filter({ hasText: 'Saved.' })).toBeVisible();
  await expect(page.getByRole('heading', { name: questionText })).toHaveCount(0);
  const card = host.locator('.admin-list article').filter({ has: host.getByRole('heading', { name: questionText }) });
  await card.getByRole('button', { name: 'Publish', exact: true }).click();
  await expect(page.getByRole('heading', { name: questionText })).toBeVisible();
  await card.getByRole('button', { name: 'Discuss now', exact: true }).click();
  await expect(page.locator('.current-question')).toContainText(questionText);

  await page.context().setOffline(true);
  await card.getByRole('button', { name: 'Mark answered', exact: true }).click();
  await page.context().setOffline(false);
  await expect(page.locator('.current-question')).toHaveCount(0, { timeout: 20000 });
  await page.getByRole('button', { name: 'Answered' }).click();
  await expect(page.getByRole('heading', { name: questionText })).toBeVisible();
  await expect(page.getByRole('button', { name: `Upvote: ${questionText}`, exact: true })).toBeDisabled();

  await host.getByRole('button', { name: 'Add panellist', exact: true }).click();
  await editor.getByLabel('Name', { exact: true }).fill('Sample browser panellist');
  await editor.getByLabel('Role / introduction').fill('Sample only');
  await editor.getByLabel('Visible to audience').check();
  await editor.getByRole('button', { name: 'Save changes' }).click();
  await expect(page.getByRole('heading', { name: 'Sample browser panellist' })).toBeVisible();
  const personCard = host.locator('.admin-list article').filter({ has: host.getByRole('heading', { name: 'Sample browser panellist' }) });
  await personCard.getByRole('button', { name: 'Hide', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Sample browser panellist' })).toHaveCount(0);
  host.on('dialog', dialog => dialog.accept());
  await personCard.getByRole('button', { name: 'Delete panellist: Sample browser panellist' }).click();
  await expect(personCard).toHaveCount(0);
  await card.getByRole('button', { name: `Delete question: ${questionText}` }).click();
  await expect(card).toHaveCount(0);
  await expect(page.getByRole('heading', { name: questionText })).toHaveCount(0);

  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await expect(page.getByRole('alert')).toHaveCount(0);
  expect(errors).toEqual([]);
  await hostContext.close();
});
