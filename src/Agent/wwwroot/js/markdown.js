/**
 * markdown.js — a deliberately small, escape-first markdown renderer.
 *
 * Model output and tool output are untrusted. The ONLY thing that may ever be
 * assigned to innerHTML is the return value of renderMarkdown(), and that
 * function HTML-escapes its entire input before a single markup rule runs, so
 * no attacker-controlled tag or attribute can survive.
 *
 * Supported: fenced code, inline code, headings, bold, italic, strikethrough,
 * links (http/https/mailto only), autolinks, unordered/ordered lists,
 * blockquotes, horizontal rules, paragraphs, line breaks.
 */

export function escapeHtml(s) {
  return String(s ?? '')
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

// Placeholder sentinels. These characters cannot appear in escaped HTML text
// that we produced, so they are safe to use as extraction markers.
const CODE_MARK = '\u0000c';
const FENCE_MARK = '\u0000f';
const MARK_END = '\u0000';

/** Only allow schemes that cannot execute script. */
function safeUrl(url) {
  const u = String(url ?? '').trim();
  if (/^(https?:\/\/|mailto:)/i.test(u)) return u;
  return null;
}

/** Turn the handful of entities we produced back into characters for URL checks. */
function unescapeEntities(s) {
  return String(s)
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"')
    .replace(/&#39;/g, "'")
    .replace(/&amp;/g, '&');
}

function inline(text) {
  let out = text;

  // inline code first — its contents must not be touched by later rules
  const codes = [];
  out = out.replace(/`([^`\n]+)`/g, (_, code) => {
    codes.push(code);
    return CODE_MARK + (codes.length - 1) + MARK_END;
  });

  // [label](url)
  out = out.replace(/\[([^\]\n]+)\]\(([^)\s]+)\)/g, (m, label, url) => {
    const safe = safeUrl(unescapeEntities(url));
    if (!safe) return label;
    return '<a href="' + escapeHtml(safe) + '" target="_blank" rel="noopener noreferrer nofollow">' + label + '</a>';
  });

  // bare URLs (the text is already escaped, so & appears as &amp;)
  out = out.replace(/(^|[\s(])(https?:\/\/[^\s<)]+)/g, (m, pre, url) => {
    const trimmed = url.replace(/[.,;:!?]+$/, '');
    const tail = url.slice(trimmed.length);
    const safe = safeUrl(unescapeEntities(trimmed));
    if (!safe) return m;
    return pre + '<a href="' + escapeHtml(safe) + '" target="_blank" rel="noopener noreferrer nofollow">' + trimmed + '</a>' + tail;
  });

  out = out.replace(/\*\*\*([^*\n]+)\*\*\*/g, '<strong><em>$1</em></strong>');
  out = out.replace(/\*\*([^*\n]+)\*\*/g, '<strong>$1</strong>');
  out = out.replace(/(^|[^*\w])\*([^*\n]+)\*(?!\*)/g, '$1<em>$2</em>');
  out = out.replace(/(^|[^_\w])_([^_\n]+)_(?![\w_])/g, '$1<em>$2</em>');
  out = out.replace(/~~([^~\n]+)~~/g, '<del>$1</del>');

  out = out.replace(new RegExp(CODE_MARK + '(\\d+)' + MARK_END, 'g'), (_, i) => '<code>' + codes[Number(i)] + '</code>');
  return out;
}

/**
 * @param {string} src raw, untrusted text
 * @returns {string} HTML that is safe to assign to innerHTML
 */
export function renderMarkdown(src) {
  const escaped = escapeHtml(src).replace(/\r\n?/g, '\n');

  // fenced code blocks are extracted before any block parsing
  const blocks = [];
  const withoutFences = escaped.replace(/```([^\n`]*)\n?([\s\S]*?)(?:```|$)/g, (_, lang, code) => {
    blocks.push({ lang: String(lang || '').trim().replace(/[^\w.+-]/g, ''), code });
    return '\n' + FENCE_MARK + (blocks.length - 1) + MARK_END + '\n';
  });

  const lines = withoutFences.split('\n');
  const html = [];
  let listType = null; // 'ul' | 'ol' | null
  let paragraph = [];
  let quote = [];

  const flushParagraph = () => {
    if (paragraph.length) {
      html.push('<p>' + inline(paragraph.join('\n')).replace(/\n/g, '<br>') + '</p>');
      paragraph = [];
    }
  };
  const flushList = () => {
    if (listType) { html.push('</' + listType + '>'); listType = null; }
  };
  const flushQuote = () => {
    if (quote.length) {
      html.push('<blockquote>' + inline(quote.join('\n')).replace(/\n/g, '<br>') + '</blockquote>');
      quote = [];
    }
  };
  const flushAll = () => { flushParagraph(); flushList(); flushQuote(); };

  const fenceRe = new RegExp('^' + FENCE_MARK + '(\\d+)' + MARK_END + '$');

  for (const raw of lines) {
    const trimmed = raw.trim();

    const fence = fenceRe.exec(trimmed);
    if (fence) {
      flushAll();
      const b = blocks[Number(fence[1])];
      html.push('<pre class="code"' + (b.lang ? ' data-lang="' + b.lang + '"' : '') + '><code>' + b.code.replace(/\n$/, '') + '</code></pre>');
      continue;
    }

    if (trimmed === '') { flushAll(); continue; }

    if (/^(---+|\*\*\*+|___+)$/.test(trimmed)) { flushAll(); html.push('<hr>'); continue; }

    const heading = /^(#{1,6})\s+(.*)$/.exec(trimmed);
    if (heading) {
      flushAll();
      const level = Math.min(6, heading[1].length + 1); // h1 -> h2, page keeps its own h1
      html.push('<h' + level + '>' + inline(heading[2]) + '</h' + level + '>');
      continue;
    }

    const bq = /^&gt;\s?(.*)$/.exec(trimmed);
    if (bq) { flushParagraph(); flushList(); quote.push(bq[1]); continue; }
    flushQuote();

    const ul = /^[-*+]\s+(.*)$/.exec(trimmed);
    const ol = /^(\d+)[.)]\s+(.*)$/.exec(trimmed);
    if (ul || ol) {
      flushParagraph();
      const want = ul ? 'ul' : 'ol';
      if (listType !== want) { flushList(); html.push('<' + want + '>'); listType = want; }
      html.push('<li>' + inline(ul ? ul[1] : ol[2]) + '</li>');
      continue;
    }
    flushList();

    paragraph.push(raw.replace(/^\s+/, ''));
  }

  flushAll();
  return html.join('\n');
}

/** Pretty-print JSON when possible, otherwise return the original text. */
export function prettyJson(text) {
  if (typeof text !== 'string') {
    try { return JSON.stringify(text, null, 2); } catch { return String(text); }
  }
  const t = text.trim();
  if (!t || !/^[[{]/.test(t)) return text;
  try { return JSON.stringify(JSON.parse(t), null, 2); } catch { return text; }
}
