import { test, expect, type Page } from '@playwright/test';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import type { Board } from '../src/types';

test('audience speaker and group clocks warn at 15% with a steady reduced-motion alternative', async ({ page }, testInfo) => {
  await join(page, 'Sample timer viewer');
  const start = new Date('2030-12-16T16:00:00Z');
  await page.clock.install();
  await page.clock.pauseAt(start);
  let groupDiscussion = false;
  let duration = 180;
  const speakerId = randomUUID();
  const questionId = randomUUID();
  await page.route('**/api/board', async route => {
    const response = await route.fetch();
    const board: Board = await response.json();
    await route.fulfill({ json: { ...board, currentQuestionId: questionId, groupDiscussion,
      timerStartedAt: start.toISOString(), serverTime: start.toISOString(), timerDurationSeconds: duration,
      panellists: [...board.panellists, { id: speakerId, name: 'Sample timer speaker', role: 'Panellist',
        bio: '', visible: true, sortOrder: 0, photoUrl: null }],
      questions: [...board.questions, { id: questionId, text: 'Sample timer warning', category: 'Browser-only preview',
        visible: true, answered: false, sortOrder: 0, score: 0, myScore: null, speakerId, speakerIds: [speakerId] }],
    } });
  });
  for (const mode of ['speaker', 'group']) {
    groupDiscussion = mode === 'group';
    duration = groupDiscussion ? 240 : 180;
    await page.clock.setSystemTime(start);
    await page.emulateMedia({ reducedMotion: 'no-preference' });
    await page.reload();
    const timer = page.getByRole('timer');
    await expect(timer).toBeVisible();
    await page.clock.fastForward(duration * 850 - 250);
    await expect(page.getByText('Time remaining', { exact: true })).toBeVisible();
    await expect(page.locator('.timer-warning')).toHaveCount(0);
    await page.clock.fastForward(250);
    await expect(page.getByText('Time running low', { exact: true })).toBeVisible();
    await expect(timer).toHaveCSS('animation-name', 'timer-warning');
    await expect(timer).toHaveCSS('animation-duration', '1.2s');
    await timer.evaluate(element => {
      const animation = element.getAnimations()[0];
      animation.pause();
      animation.currentTime = 600;
    });
    await expect(timer).toHaveCSS('transform', 'matrix(1.25, 0, 0, 1.25, 0, 0)');
    await page.locator('.current-question').screenshot({ path: testInfo.outputPath(`${mode}-timer-warning.png`),
      animations: 'allow' });
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await expect(timer).toHaveCSS('animation-name', 'none');
    await expect(timer).toHaveCSS('transform', 'none');
    await expect(page.getByText('Time running low', { exact: true })).toBeVisible();
    await page.clock.fastForward(duration * 150);
    await expect(page.getByText("Time's up")).toBeVisible();
    await expect(page.locator('.timer-warning')).toHaveCount(0);
  }
});

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

async function adminWrite(host: Page, url: string, method: string, data?: unknown) {
  const board: Board = await (await host.request.get('/api/admin/board')).json();
  const session = await (await host.request.get('/api/session')).json();
  const response = await host.request.fetch(url, { method, data,
    headers: { 'X-CSRF-TOKEN': session.csrfToken, 'If-Match': `"${board.revision}"` } });
  expect(response.ok(), await response.text()).toBe(true);
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

test('hosts assign uploaded speaker portraits and synchronise speaker and non-voting group timers', async ({ browser, page }) => {
  test.setTimeout(60000);
  const hostContext = await browser.newContext();
  const host = await hostContext.newPage();
  host.setDefaultTimeout(10000);
  await hostDesk(host);
  const original: Board = await (await host.request.get('/api/admin/board')).json();
  const secondContext = await browser.newContext();
  const second = await secondContext.newPage();
  let personId: string | null = null;
  let questionId: string | null = null;
  try {
    await join(page, 'Speaker timer viewer');
    await page.getByRole('button', { name: 'Sign in as demo voter' }).click();
    await join(second, 'Independent timer viewer');
    await host.getByRole('button', { name: 'Edit timer lengths' }).click();
    await host.getByLabel('Speaker timer (seconds)', { exact: true }).fill('30');
    await host.getByLabel('Group discussion timer (seconds)', { exact: true }).fill('60');
    await host.locator('.timing-form').getByRole('button', { name: 'Save changes' }).click();
    const name = `Sample timed speaker ${Date.now()}`;
    await host.getByRole('button', { name: 'Add panellist', exact: true }).click();
    const editor = host.locator('.editor-form');
    await editor.getByLabel('Name', { exact: true }).fill(name);
    await editor.getByLabel('Role / introduction').fill('Browser test sample only');
    await editor.getByLabel('Visible to audience').check();
    await editor.getByRole('button', { name: 'Save changes' }).click();
    const person = host.locator('.admin-list article').filter({ has: host.getByRole('heading', { name, exact: true }) });
    await expect(person).toBeVisible();
    personId = (await (await host.request.get('/api/admin/board')).json() as Board).panellists.find(p => p.name === name)!.id;
    await person.getByRole('button', { name: 'Upload photo', exact: true }).click();
    await host.getByLabel('Panellist image', { exact: true }).setInputFiles(path.resolve('public', 'branding', 'fireside-scene.jpg'));
    await host.locator('.photo-form').getByRole('button', { name: 'Save changes' }).click();
    await expect(person.getByRole('img', { name: `Portrait of ${name}` })).toBeVisible();
    const text = `Sample timed discussion ${Date.now()}`;
    await host.getByRole('button', { name: 'Add question', exact: true }).click();
    await editor.getByLabel('Question', { exact: true }).fill(text);
    await editor.getByRole('combobox', { name: 'Speaker', exact: true }).selectOption({ label: name });
    for (let index = 0; index < original.panellists.length; index++)
      await editor.getByRole('button', { name: `Move ${name} earlier`, exact: true }).click();
    await editor.getByLabel('Visible to audience').check();
    await editor.getByRole('button', { name: 'Save changes' }).click();
    const question = host.locator('.admin-list article').filter({ has: host.getByRole('heading', { name: text, exact: true }) });
    await expect(question).toBeVisible();
    questionId = (await (await host.request.get('/api/admin/board')).json() as Board).questions.find(q => q.text === text)!.id;
    await host.reload();
    const saved: Board = await (await host.request.get('/api/admin/board')).json();
    const ordered = saved.questions.find(q => q.id === questionId)!;
    expect(ordered.speakerIds[0]).toBe(personId);
    await question.getByRole('combobox', { name: 'Select speaker', exact: true }).selectOption({ label: name });
    for (const viewer of [page, second]) {
      await expect(viewer.locator('.current-question')).toContainText(name);
      await expect(viewer.getByText('Timer not started', { exact: true })).toBeVisible();
    }
    await question.getByRole('button', { name: 'Start speaker timer', exact: true }).click();
    for (const viewer of [page, second]) {
      await expect(viewer.locator('.current-question')).toContainText(text);
      await expect(viewer.getByRole('img', { name: `Portrait of ${name}`, exact: true })).toBeVisible();
      await expect.poll(() => viewer.getByRole('img', { name: `Portrait of ${name}`, exact: true })
        .evaluate((el: HTMLImageElement) => el.naturalWidth)).toBeGreaterThan(0);
    }
    const started: Board = await (await page.request.get('/api/board')).json();
    expect(started.timerDurationSeconds).toBe(30);
    const remaining = async (viewer: Page) => {
      const value = await viewer.getByRole('timer').textContent();
      const [minutes, seconds] = value!.split(':').map(Number);
      return minutes * 60 + seconds;
    };
    expect(Math.abs(await remaining(page) - await remaining(second))).toBeLessThanOrEqual(1);
    const before = await remaining(page);
    await secondContext.setOffline(true);
    await expect.poll(() => remaining(page)).toBeLessThan(before);
    await secondContext.setOffline(false);
    await second.reload();
    await expect(second.getByRole('timer')).toBeVisible();
    const recovered: Board = await (await second.request.get('/api/board')).json();
    expect(recovered.timerStartedAt).toBe(started.timerStartedAt);
    expect(Math.abs(await remaining(page) - await remaining(second))).toBeLessThanOrEqual(1);
    const nextPerson = saved.panellists.find(p => p.id === ordered.speakerIds.find(id =>
      id !== personId && saved.panellists.some(person => person.id === id && person.visible)))!;
    await question.getByRole('button', { name: `Next speaker: ${text}`, exact: true }).click();
    for (const viewer of [page, second]) {
      await expect(viewer.locator('.current-question')).toContainText(nextPerson.name);
      await expect(viewer.getByText('Timer not started', { exact: true })).toBeVisible();
    }
    await question.getByRole('button', { name: `Previous speaker: ${text}`, exact: true }).click();
    await expect(second.locator('.current-question')).toContainText(name);
    await expect(second.getByText('Timer not started', { exact: true })).toBeVisible();
    await question.getByRole('button', { name: `Previous speaker: ${text}`, exact: true }).click();
    const lastId = ordered.speakerIds.filter(id => saved.panellists.some(p => p.id === id && p.visible)).at(-1)!;
    await expect(second.locator('.current-question')).toContainText(saved.panellists.find(p => p.id === lastId)!.name);
    await question.getByRole('button', { name: `Next speaker: ${text}`, exact: true }).click();
    await expect(second.locator('.current-question')).toContainText(name);
    await question.getByRole('button', { name: 'Start group discussion', exact: true }).click();
    for (const viewer of [page, second]) {
      await expect(viewer.locator('.current-question').getByText('Group discussion', { exact: true })).toBeVisible();
      await expect(viewer.locator('.current-question button')).toHaveCount(0);
    }
    const group: Board = await (await page.request.get('/api/board')).json();
    expect(group.timerDurationSeconds).toBe(60);
    expect(await remaining(page)).toBeGreaterThan(45);
    const session = await (await page.request.get('/api/session')).json();
    const attempt = await page.request.post(`/api/questions/${questionId}/vote`, {
      headers: { 'X-CSRF-TOKEN': session.csrfToken }, data: { requestId: randomUUID(), kind: 'epic' },
    });
    expect(attempt.status()).toBe(409);
    await host.getByRole('button', { name: 'Stop timer', exact: true }).click();
    await expect(page.getByText('Timer not started', { exact: true })).toBeVisible();
    await expect(page.locator('.current-question button')).toHaveCount(0);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
  } finally {
    if (questionId) await adminWrite(host, `/api/admin/questions/${questionId}`, 'DELETE');
    if (personId) await adminWrite(host, `/api/admin/panellists/${personId}`, 'DELETE');
    const latest: Board = await (await host.request.get('/api/admin/board')).json();
    if (latest.speakerSeconds === 30 && latest.groupSeconds === 60)
      await adminWrite(host, '/api/admin/timing', 'PUT', { speakerSeconds: original.speakerSeconds, groupSeconds: original.groupSeconds });
    await secondContext.close();
    await hostContext.close();
  }
});

test('host score edits persist and scoreboard visibility updates independent viewers live', async ({ browser, page }, testInfo) => {
  test.setTimeout(60000);
  const hostContext = await browser.newContext();
  const secondContext = await browser.newContext();
  const host = await hostContext.newPage();
  host.setDefaultTimeout(10000);
  const second = await secondContext.newPage();
  await hostDesk(host);
  const disclosure = host.locator('.speaker-scores > summary');
  await expect(host.getByRole('button', { name: /^(Show|Hide) scoreboard$/ })).toBeHidden();
  await host.locator('.speaker-scores').screenshot({ path: testInfo.outputPath('host-scores-collapsed.png') });
  await disclosure.focus();
  await host.keyboard.press('Enter');
  await expect(host.locator('.speaker-scores')).toHaveAttribute('open', '');
  await host.keyboard.press('Enter');
  await expect(host.locator('.speaker-scores')).not.toHaveAttribute('open');
  await disclosure.click();
  const original: Board = await (await host.request.get('/api/admin/board')).json();
  let personId: string | null = null;
  let questionId: string | null = null;
  try {
    if (original.scoreboardVisible)
      await adminWrite(host, '/api/admin/scoreboard', 'PUT', { visible: false });
    await join(page, 'Scoreboard viewer');
    await join(second, 'Independent scoreboard viewer');
    for (const viewer of [page, second]) {
      await expect(viewer.getByRole('tab', { name: 'Scoreboard', exact: true })).toHaveCount(0);
      expect(await (await viewer.request.get('/api/board')).json()).not.toHaveProperty('scoreboard');
    }
    const name = `Sample scoreboard speaker ${Date.now()}`;
    const text = `Sample scoreboard question ${Date.now()}`;
    await adminWrite(host, '/api/admin/panellists', 'POST',
      { name, role: 'Test fixture only', bio: '', visible: true, sortOrder: 9999 });
    let board: Board = await (await host.request.get('/api/admin/board')).json();
    personId = board.panellists.find(person => person.name === name)!.id;
    const person = host.locator('.admin-list article').filter({ has: host.getByRole('heading', { name, exact: true }) });
    await person.getByRole('button', { name: 'Upload photo', exact: true }).click();
    await host.getByLabel('Panellist image', { exact: true }).setInputFiles(path.resolve('public', 'branding', 'fireside-scene.jpg'));
    await host.locator('.photo-form').getByRole('button', { name: 'Save changes', exact: true }).click();
    await expect(person.getByRole('img', { name: `Portrait of ${name}`, exact: true })).toBeVisible();
    await adminWrite(host, '/api/admin/questions', 'POST',
      { text, category: 'Test fixture only', visible: true, answered: false, sortOrder: 9999, speakerId: personId });
    board = await (await host.request.get('/api/admin/board')).json();
    questionId = board.questions.find(question => question.text === text)!.id;
    await host.getByRole('button', { name: `Edit points for ${name}: ${text}`, exact: true }).click();
    const form = host.locator('.score-edit-form');
    await expect(form.getByLabel('Points', { exact: true })).toHaveValue('0');
    await form.getByLabel('Points', { exact: true }).fill('8');
    await form.getByLabel('Reason for change', { exact: true }).fill('Sample scoreboard verification');
    await form.getByRole('button', { name: 'Save changes', exact: true }).click();
    await expect(form).toHaveCount(0);
    await host.getByRole('button', { name: 'Show scoreboard', exact: true }).click();
    for (const [index, viewer] of [page, second].entries()) {
      await expect(viewer.getByRole('tab', { name: 'Scoreboard', exact: true })).toBeVisible();
      await viewer.getByRole('tab', { name: 'Scoreboard', exact: true }).click();
      const row = viewer.getByRole('tabpanel', { name: 'Scoreboard', exact: true }).getByRole('region', { name: text, exact: true });
      await expect(row.getByLabel(`${name}: 8 points`, { exact: true })).toBeVisible();
      await expect(row.getByRole('img', { name: `Portrait of ${name}`, exact: true })).toBeVisible();
      await expect.poll(() => row.getByRole('img', { name: `Portrait of ${name}`, exact: true })
        .evaluate((image: HTMLImageElement) => image.naturalWidth)).toBeGreaterThan(0);
      expect(await viewer.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
      await viewer.getByRole('tabpanel', { name: 'Scoreboard', exact: true })
        .screenshot({ path: testInfo.outputPath(`scoreboard-${index}.png`) });
    }
    await host.locator('.speaker-scores').screenshot({ path: testInfo.outputPath('host-scores-expanded.png') });
    await second.reload();
    await second.getByRole('tab', { name: 'Live', exact: true }).focus();
    await second.keyboard.press('End');
    await expect(second.getByRole('tab', { name: 'Scoreboard', exact: true })).toHaveAttribute('aria-selected', 'true');
    await expect(second.getByRole('tabpanel', { name: 'Scoreboard', exact: true })
      .getByRole('region', { name: text, exact: true }).getByLabel(`${name}: 8 points`, { exact: true })).toBeVisible();
    await host.getByRole('button', { name: 'Hide scoreboard', exact: true }).click();
    for (const viewer of [page, second]) {
      await expect(viewer.getByRole('tab', { name: 'Scoreboard', exact: true })).toHaveCount(0);
      await expect(viewer.getByRole('tab', { name: 'Live', exact: true })).toHaveAttribute('aria-selected', 'true');
      expect(await (await viewer.request.get('/api/board')).json()).not.toHaveProperty('scoreboard');
      expect(await viewer.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
    }
  } finally {
    if (questionId) await adminWrite(host, `/api/admin/questions/${questionId}`, 'DELETE');
    if (personId) await adminWrite(host, `/api/admin/panellists/${personId}`, 'DELETE');
    const latest: Board = await (await host.request.get('/api/admin/board')).json();
    if (latest.scoreboardVisible !== original.scoreboardVisible)
      await adminWrite(host, '/api/admin/scoreboard', 'PUT', { visible: original.scoreboardVisible });
    await secondContext.close();
    await hostContext.close();
  }
});
