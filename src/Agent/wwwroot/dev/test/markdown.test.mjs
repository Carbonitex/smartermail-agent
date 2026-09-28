import test from 'node:test';
import assert from 'node:assert/strict';
import { renderMarkdown, escapeHtml, prettyJson } from '../../js/markdown.js';

test('escapes everything dangerous before any markup rule runs', () => {
  const html = renderMarkdown('<img src=x onerror=alert(1)> & "quotes" and \'apostrophes\'');
  assert.ok(!html.includes('<img'));
  assert.ok(html.includes('&lt;img'));
  assert.ok(html.includes('&quot;quotes&quot;'));
  assert.ok(html.includes('&#39;apostrophes&#39;'));
  // the only tags in the output are ones this renderer emitted itself
  const tags = [...html.matchAll(/<\/?([a-z0-9]+)/gi)].map((m) => m[1].toLowerCase());
  assert.deepEqual([...new Set(tags)], ['p']);
});

test('a script tag in a fenced code block stays inert', () => {
  const html = renderMarkdown('```html\n<script>alert(1)</script>\n```');
  assert.ok(html.startsWith('<pre class="code" data-lang="html">'));
  assert.ok(!html.includes('<script>'));
  assert.ok(html.includes('&lt;script&gt;'));
});

test('javascript: and data: links are stripped to plain text', () => {
  const html = renderMarkdown('[click](javascript:alert(1)) and [x](data:text/html,<script>)');
  assert.ok(!html.includes('href'));
  assert.ok(html.includes('click'));
});

test('http links render with rel and target', () => {
  const html = renderMarkdown('see [docs](https://example.com/a?b=1&c=2)');
  assert.match(html, /<a href="https:\/\/example\.com\/a\?b=1&amp;c=2" target="_blank" rel="noopener noreferrer nofollow">docs<\/a>/);
});

test('bare urls are autolinked, trailing punctuation excluded', () => {
  const html = renderMarkdown('go to https://example.com/x.');
  assert.match(html, /<a href="https:\/\/example\.com\/x"/);
  assert.ok(html.endsWith('.</p>'));
});

test('an attribute break-out attempt inside a link label cannot escape', () => {
  const html = renderMarkdown('[a"onmouseover="alert(1)](https://example.com)');
  assert.ok(!html.includes('onmouseover="alert'));
  assert.ok(html.includes('&quot;onmouseover=&quot;'));
});

test('bold, italic, strike and inline code', () => {
  const html = renderMarkdown('**b** and *i* and _u_ and ~~s~~ and `code<>`');
  assert.ok(html.includes('<strong>b</strong>'));
  assert.ok(html.includes('<em>i</em>'));
  assert.ok(html.includes('<em>u</em>'));
  assert.ok(html.includes('<del>s</del>'));
  assert.ok(html.includes('<code>code&lt;&gt;</code>'));
});

test('inline code contents are not re-processed as markdown', () => {
  const html = renderMarkdown('`**not bold**`');
  assert.ok(html.includes('<code>**not bold**</code>'));
  assert.ok(!html.includes('<strong>'));
});

test('lists, headings, blockquotes and rules', () => {
  const html = renderMarkdown([
    '## Unread',
    '- one',
    '- two',
    '',
    '1. first',
    '2. second',
    '',
    '> quoted',
    '',
    '---'
  ].join('\n'));
  assert.ok(html.includes('<h3>Unread</h3>'));
  assert.ok(html.includes('<ul>\n<li>one</li>\n<li>two</li>\n</ul>'));
  assert.ok(html.includes('<ol>\n<li>first</li>'));
  assert.ok(html.includes('<blockquote>quoted</blockquote>'));
  assert.ok(html.includes('<hr>'));
});

test('paragraphs keep single newlines as breaks', () => {
  const html = renderMarkdown('line one\nline two\n\npara two');
  assert.equal(html, '<p>line one<br>line two</p>\n<p>para two</p>');
});

test('an unterminated code fence (mid-stream) still renders', () => {
  const html = renderMarkdown('text\n```js\nconst a = 1;');
  assert.ok(html.includes('<pre class="code" data-lang="js"><code>const a = 1;</code></pre>'));
});

test('renderMarkdown never throws on odd input', () => {
  for (const s of ['', null, undefined, '```', '***', '[', '`', '> ', '- ', '#'.repeat(20)]) {
    assert.equal(typeof renderMarkdown(s), 'string');
  }
});

test('escapeHtml covers the five characters that matter', () => {
  assert.equal(escapeHtml(`<>&"'`), '&lt;&gt;&amp;&quot;&#39;');
});

test('prettyJson formats JSON and passes other text through', () => {
  assert.equal(prettyJson('{"a":1}'), '{\n  "a": 1\n}');
  assert.equal(prettyJson('not json'), 'not json');
  assert.equal(prettyJson('{broken'), '{broken');
});
