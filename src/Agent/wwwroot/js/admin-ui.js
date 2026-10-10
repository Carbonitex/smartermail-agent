/**
 * admin-ui.js — task invites from the Profile menu, for profiles listed in the server's
 * ADMIN_PROFILES (the view says so with `admin: true`). The same operations as the operator's CLI:
 * make a code (shown once), list and revoke codes, see and revoke which profiles may use tasks.
 * The server answers 404 to anyone else, so nothing here is reachable without being on the list.
 */

import * as api from './api.js';

const fmt = (iso) => (iso ? new Date(iso).toLocaleDateString() : '');

/**
 * The admin block. ctx: { notice(text, kind), busy(control, fn) }. Loads its own data and redraws
 * itself in place, so the rest of the menu is left alone.
 */
export function adminSection(ctx) {
  const box = document.createElement('div');
  box.className = 'admin-box';
  let fresh = null;   // { code, id } of the code made just now, shown until the next redraw after Done

  const draw = async () => {
    let data;
    try {
      data = await api.adminInvites();
    } catch (err) {
      box.replaceChildren(para(`Could not load invites: ${err.message}`, 'error'));
      return;
    }
    const parts = [];
    if (!data.inviteOnly) parts.push(para('This server is not invite-only (TASKS_ACCESS=open): every profile may use tasks, so codes change nothing yet.', 'warn'));

    if (fresh) {
      parts.push(para('New invite code. It is shown only now; send it to the person it is for:'));
      const code = document.createElement('div');
      code.className = 'code-box';
      code.textContent = fresh.code;
      const row = document.createElement('div');
      row.className = 'profile-row';
      row.append(
        button('Copy', async (b) => {
          try {
            await navigator.clipboard.writeText(fresh.code);
            b.textContent = 'Copied';
          } catch {
            ctx.notice('Copy failed: select the code and copy it by hand.', 'error');
          }
        }, true),
        button('Done', () => { fresh = null; draw(); }));
      parts.push(code, row);
    }

    // New code
    const form = document.createElement('div');
    form.className = 'profile-row';
    const note = input('text', 'Note, e.g. who it is for', 80);
    const uses = input('number', 'Uses', 5);
    uses.value = '1';
    uses.min = '1';
    uses.style.maxWidth = '64px';
    uses.title = 'How many profiles may use it';
    const days = input('number', 'Days', 5);
    days.min = '1';
    days.style.maxWidth = '64px';
    days.title = 'Expires after this many days (empty = never)';
    form.append(note, uses, days, button('Create', (b) => ctx.busy(b, async () => {
      const made = await api.adminCreateInvite({
        note: note.value.trim() || null,
        uses: Number(uses.value) || 1,
        days: days.value ? Number(days.value) : null
      });
      fresh = { code: made.code, id: made.invite.id };
      await draw();
    }), true));
    parts.push(form);

    // Codes
    if (data.invites.length) parts.push(para('Codes:'));
    for (const i of [...data.invites].reverse()) {
      const row = document.createElement('div');
      row.className = 'profile-row';
      const text = document.createElement('span');
      text.textContent = `${i.note || i.id} · ${i.state} · ${i.uses}/${i.maxUses}` +
        (i.expiresAt ? ` · until ${fmt(i.expiresAt)}` : '');
      text.title = `${i.id}, made ${fmt(i.createdAt)}`;
      row.appendChild(text);
      if (i.state !== 'revoked') {
        row.appendChild(button('Revoke', (b) => ctx.busy(b, async () => {
          await api.adminRevokeInvite(i.id, false);
          await draw();
        })));
      }
      parts.push(row);
    }

    // Profiles with access
    parts.push(para(data.access.length ? 'Profiles that may use tasks:' : 'No profile may use tasks yet.'));
    for (const a of data.access) {
      const row = document.createElement('div');
      row.className = 'profile-row';
      const text = document.createElement('span');
      text.textContent = `${a.you ? 'You' : a.profileId.slice(0, 8) + '…'} · ${a.inviteNote || (a.inviteId ? 'invite ' + a.inviteId : 'granted')} · seen ${fmt(a.lastSeenAt)}`;
      text.title = a.profileId;
      row.appendChild(text);
      const revoke = button('Revoke', null);
      revoke.addEventListener('click', () => {
        if (revoke.dataset.armed !== '1') {
          revoke.dataset.armed = '1';
          revoke.textContent = a.you ? 'Click again: revoke your own' : 'Click again: pause its tasks';
          revoke.classList.add('btn-danger');
          return;
        }
        ctx.busy(revoke, async () => {
          await api.adminRevokeAccess(a.profileId);
          await draw();
        });
      });
      row.appendChild(revoke);
      parts.push(row);
    }

    box.replaceChildren(...parts);
  };

  box.replaceChildren(para('Loading invites…'));
  draw();
  return box;
}

function para(text, kind = '') {
  const p = document.createElement('p');
  p.className = 'hint profile-hint' + (kind ? ' ' + kind : '');
  p.textContent = text;
  return p;
}

function input(type, placeholder, maxLength) {
  const i = document.createElement('input');
  i.type = type;
  i.placeholder = placeholder;
  i.className = 'profile-input';
  i.autocomplete = 'off';
  if (type === 'text') i.maxLength = maxLength;
  return i;
}

function button(text, onClick, primary = false) {
  const b = document.createElement('button');
  b.type = 'button';
  b.className = 'btn btn-small ' + (primary ? 'btn-primary' : 'btn-ghost');
  b.textContent = text;
  if (onClick) b.addEventListener('click', () => onClick(b));
  return b;
}
