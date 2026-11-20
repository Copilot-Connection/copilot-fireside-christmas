import { test, expect, type Page } from '@playwright/test';
import path from 'node:path';

async function join(page: Page, name: string) {
  await page.goto('/');
  await page.getByLabel('What should we call you?').fill(name);
  await page.getByRole('button', { name: 'Pull up a chair' }).click();
  await expect(page.getByText('Connected to the fireside')).toBeVisible();
}

async function hostDesk(page: Page) {
  await page.goto('/admin');
  await page.getByRole('button', { name: 'Enter demo host desk' }).click();
  await expect(page.getByRole('heading', { name: 'Keep the conversation glowing.' })).toBeVisible();
}

test('repeatable scores and matching transient reactions reach independent browsers', async ({ browser, page }) => {
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  await join(page, 'Browser participant');
  await expect(page.getByRole('tab', { name: 'Live', exact: true })).toHaveAttribute('aria-selected', 'true');
  await page.getByRole('tab', { name: 'Questions & votes' }).click();
  await expect(page.locator('.question-card button')).toHaveCount(0);
  await expect(page.locator('.personal-points').first()).toHaveText('Sign in to see');
  await page.getByRole('tab', { name: 'Live', exact: true }).click();
  await page.getByRole('button', { name: 'Sign in as demo voter' }).click();
  await expect(page.locator('.site-header').getByRole('button', { name: 'Sign out of Microsoft' })).toBeVisible();

  const hostContext = await browser.newContext();
  const host = await hostContext.newPage();
  await hostDesk(host);
  await host.getByRole('button', { name: 'Add question', exact: true }).click();
  const questionText = `A sample Christmas point ${Date.now()}`;
  const editor = host.locator('.editor-form');
  await editor.getByLabel('Question', { exact: true }).fill(questionText);
  await editor.getByLabel('Category').fill('Sample browser test');
  await editor.getByRole('button', { name: 'Save changes' }).click();
  await page.getByRole('tab', { name: 'Questions & votes' }).click();
  await expect(page.getByRole('heading', { name: questionText })).toHaveCount(0);
  const hostCard = host.locator('.admin-list article').filter({ has: host.getByRole('heading', { name: questionText }) });
  await hostCard.getByRole('button', { name: 'Publish', exact: true }).click();
  const firstCard = page.locator('.question-card').filter({ hasText: questionText });
  await expect(firstCard).toBeVisible();
  await expect(firstCard.locator('.total-points')).toHaveText('0');
  await expect(firstCard.locator('.personal-points')).toHaveText('0');
  await expect(firstCard.getByRole('button')).toHaveCount(0);
  await hostCard.getByRole('button', { name: 'Discuss now', exact: true }).click();
  await page.getByRole('tab', { name: 'Live', exact: true }).click();
  const live = page.locator('.current-question');
  await expect(live).toContainText(questionText);

  const secondContext = await browser.newContext({ reducedMotion: 'reduce' });
  const second = await secondContext.newPage();
  await join(second, 'Another browser, same demo identity');
  await second.getByRole('button', { name: 'Sign in as demo voter' }).click();
  const secondLive = second.locator('.current-question');
  await expect(secondLive).toContainText(questionText);
  const secondCard = second.locator('.question-card').filter({ hasText: questionText });
  const actions = [
    { label: 'Like the point', kind: 'like', score: 1 },
    { label: 'Like the point', kind: 'like', score: 2 },
    { label: "Don't like this point", kind: 'dislike', score: 1 },
    { label: 'EPIC POINT!', kind: 'epic', score: 4 },
  ];
  for (const [index, action] of actions.entries()) {
    await expect(page.locator('.reaction-burst')).toHaveCount(0);
    await live.getByRole('button', { name: `${action.label}: ${questionText}`, exact: true }).click();
    const reaction = page.locator(`.reaction-burst[data-kind="${action.kind}"]`);
    await expect(reaction).toBeAttached();
    const id = await reaction.getAttribute('data-reaction-id');
    const matching = second.locator(`.reaction-burst[data-reaction-id="${id}"]`);
    await expect(matching).toBeAttached();
    await expect(live.locator('output')).toHaveText(`${action.score} ${action.score === 1 ? 'point' : 'points'}`);
    await expect(secondLive.locator('output')).toHaveText(`${action.score} ${action.score === 1 ? 'point' : 'points'}`);
    await expect(firstCard.locator('.personal-points')).toHaveText(String(action.score));
    await expect(secondCard.locator('.personal-points')).toHaveText(String(action.score));
    expect(await matching.evaluate(el => getComputedStyle(el).animationName)).toBe('none');
    if (index === 0) {
      const end = await reaction.evaluate(el => {
        const animation = el.getAnimations()[0];
        animation.pause();
        animation.currentTime = Number(animation.effect!.getTiming().duration);
        const style = getComputedStyle(el);
        return { y: new DOMMatrix(style.transform).m42, opacity: style.opacity, viewport: innerHeight };
      });
      expect(Math.abs(end.y + end.viewport * .33)).toBeLessThan(2);
      expect(end.opacity).toBe('0');
    }
    await expect(reaction).toHaveCount(0);
    await expect(matching).toHaveCount(0);
  }
  await secondLive.getByRole('button', { name: `EPIC POINT!: ${questionText}`, exact: true }).click();
  await expect(firstCard.locator('.total-points')).toHaveText('7');
  await page.reload();
  await expect(page.locator('.reaction-burst')).toHaveCount(0);
  await expect(live.getByRole('button', { name: `Like the point: ${questionText}` })).toBeEnabled();
  await page.getByRole('tab', { name: 'Questions & votes' }).click();
  await expect(firstCard.locator('.total-points')).toHaveText('7');
  await expect(firstCard.locator('.personal-points')).toHaveText('7');
  await expect(firstCard.getByRole('button')).toHaveCount(0);
  await second.goto('/admin');
  await expect(second.getByRole('alert')).toContainText('configured administrative tenant');
  await secondContext.close();

  await page.getByRole('tab', { name: 'Live', exact: true }).click();
  await expect(page.locator('.current-question')).toContainText(questionText);
  const size = await page.locator('.current-question').evaluate(el => ({
    bottom: el.getBoundingClientRect().bottom + scrollY, viewport: innerHeight,
  }));
  expect(size.bottom).toBeGreaterThanOrEqual(size.viewport - 1);
  const liveAction = page.locator('.current-question').getByRole('button', { name: `EPIC POINT!: ${questionText}`, exact: true });
  await liveAction.scrollIntoViewIfNeeded();
  await expect(liveAction).toBeInViewport();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);

  await page.context().setOffline(true);
  await hostCard.getByRole('button', { name: 'Mark answered', exact: true }).click();
  await page.context().setOffline(false);
  await expect(page.locator('.current-question')).toHaveCount(0, { timeout: 20000 });
  await expect(page.locator('.reaction-burst')).toHaveCount(0);
  await page.getByRole('tab', { name: 'Questions & votes' }).click();
  await page.getByRole('button', { name: 'Answered', exact: true }).click();
  await expect(page.getByRole('heading', { name: questionText })).toBeVisible();
  await expect(firstCard.getByRole('button')).toHaveCount(0);
  await expect(firstCard.locator('.personal-points')).toHaveText('7');

  const personName = `Sample browser panellist ${Date.now()}`;
  await host.getByRole('button', { name: 'Add panellist', exact: true }).click();
  await editor.getByLabel('Name', { exact: true }).fill(personName);
  await editor.getByLabel('Role / introduction').fill('Sample only');
  await editor.getByLabel('Visible to audience').check();
  await editor.getByRole('button', { name: 'Save changes' }).click();
  await page.getByRole('tab', { name: 'Panellists' }).click();
  await expect(page.getByRole('heading', { name: personName })).toBeVisible();
  const personCard = host.locator('.admin-list article').filter({ has: host.getByRole('heading', { name: personName }) });
  await personCard.getByRole('button', { name: 'Hide', exact: true }).click();
  await expect(page.getByRole('heading', { name: personName })).toHaveCount(0);
  host.on('dialog', dialog => dialog.accept());
  await personCard.getByRole('button', { name: `Delete panellist: ${personName}` }).click();
  await expect(personCard).toHaveCount(0);
  await hostCard.getByRole('button', { name: `Delete question: ${questionText}` }).click();
  await expect(hostCard).toHaveCount(0);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  expect(errors).toEqual([]);
  await hostContext.close();
});

test('local default artwork, durable host uploads and keyboard tabs stay on the same page', async ({ browser, page }) => {
  const hostContext = await browser.newContext();
  const host = await hostContext.newPage();
  await hostDesk(host);
  const showLanding = host.getByRole('button', { name: 'Show landing page', exact: true });
  if (await showLanding.isEnabled()) {
    await showLanding.click();
    await expect(showLanding).toBeDisabled();
  }
  const reset = host.getByRole('button', { name: 'Use default artwork', exact: true });
  if (await reset.isEnabled()) {
    await reset.click();
    await expect(reset).toBeDisabled();
  }
  await page.goto('/');
  const header = page.locator('.site-header');
  await expect(header.locator('.demo-strip')).toContainText('LOCAL DEMO');
  await page.evaluate(() => document.fonts.ready);
  expect(await header.evaluate(el => el.getBoundingClientRect().height)).toBeLessThanOrEqual(128);
  await expect(page.locator('.landing-artwork img')).toHaveAttribute('src', '/branding/christmas-fireside.png');
  expect(await page.locator('.landing-artwork img').evaluate((el: HTMLImageElement) => el.complete && el.naturalWidth === 1920)).toBe(true);
  await page.getByLabel('What should we call you?').fill('Artwork viewer');
  await page.getByRole('button', { name: 'Pull up a chair' }).click();
  await expect(header.locator('.audience-bar')).toContainText('Connected to the fireside');
  await expect(page.getByText('Waiting for the host')).toBeVisible();
  const originalUrl = page.url();
  await page.getByRole('tab', { name: 'Live', exact: true }).focus();
  await page.keyboard.press('ArrowRight');
  await expect(page.getByRole('tab', { name: 'Questions & votes' })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByRole('tabpanel', { name: 'Live', exact: true })).toBeHidden();
  await page.keyboard.press('End');
  await expect(page.getByRole('tab', { name: 'Panellists' })).toHaveAttribute('aria-selected', 'true');
  await page.keyboard.press('Home');
  await expect(page.getByRole('tab', { name: 'Live', exact: true })).toHaveAttribute('aria-selected', 'true');
  expect(page.url()).toBe(originalUrl);

  await host.getByRole('button', { name: 'Change landing image', exact: true }).click();
  await host.getByLabel('Image file', { exact: true }).setInputFiles(path.resolve('public', 'branding', 'fireside-scene.jpg'));
  await host.getByLabel('Image description', { exact: true }).fill('Uploaded test artwork');
  await host.locator('.editor-form').getByRole('button', { name: 'Save changes', exact: true }).click();
  await expect(page.getByRole('img', { name: 'Uploaded test artwork', exact: true })).toBeVisible();
  await page.reload();
  await expect(page.getByRole('img', { name: 'Uploaded test artwork', exact: true })).toBeVisible();
  expect(await page.getByRole('img', { name: 'Uploaded test artwork', exact: true }).evaluate((el: HTMLImageElement) => el.naturalWidth > 0)).toBe(true);
  await reset.click();
  await expect(page.locator('.landing-artwork img')).toHaveAttribute('src', '/branding/christmas-fireside.png');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await hostContext.close();
});
