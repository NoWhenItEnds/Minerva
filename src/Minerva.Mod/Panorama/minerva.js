// Minerva HUD probe: in-game half of Minerva.
//
// It reads the HUD's own panel tree (only what the game already draws for the player) and writes it to the console,
// which the game mirrors into game/citadel/console.log when launched with -condebug. The companion app follows that
// file. Read-only: no input, no game commands, no network.
//
// Deliberately generic: no Deadlock-specific panel ids. Interpreting the tree (which label is souls, which icon is a
// hero on the minimap, ...) is the companion's job, so HUD changes in a patch never require changing this script.
//
// Panorama runs on the game's main thread, so no frame may carry much of this script's work:
// - all work (walking, diffing, console output) shares a budget of MINERVA_SLICE_MS per frame;
// - changes are detected per panel as it is visited, and removals per parent (its child list against the previous
//   walk's), so finishing a walk costs nothing proportional to the tree;
// - most of the HUD is hidden at any moment (shop, menus, tooltips: ~90% of panels in a match), so hidden subtrees are
//   only walked every MINERVA_COMPLETE_EVERY walks; in between, their last reported state stands;
// - walks are spaced so the script uses at most MINERVA_MAX_LOAD of main-thread time.
//
// Wire format, one console line per chunk:
//   MNRV3|<seq>|<chunk index>|<chunk count>|<chunk of a JSON message>
// Messages:
//   hello  script started
//   full   every panel: panels = [[parent index, name, type index, visible, x, y, w, h, text, value], ...], types = [...]
//   delta  set = [[key, type, visible, x, y, w, h, text, value], ...] for panels that changed,
//          del = [key] for panels removed, each with everything below it
//   error  an exception, with where/message
// Trailing nulls are omitted from rows. Geometry is null for panels not on screen (hidden, or not laid out yet).
// Kept to ES5 syntax to be safe with Panorama's script host.

'use strict';

var MINERVA_PROTOCOL = 3;
var MINERVA_MIN_INTERVAL_MS = 1000; // at most one walk started per second
var MINERVA_MAX_LOAD = 0.05; // fraction of main-thread time the walk may use on average
var MINERVA_COMPLETE_EVERY = 10; // walks; the others skip hidden subtrees
var MINERVA_FULL_INTERVAL_MS = 300000; // resend everything every 5 minutes (deltas otherwise)
var MINERVA_SLICE_MS = 2; // script budget per frame
var MINERVA_MAX_PANELS = 60000;
var MINERVA_CHUNK = 900; // characters of JSON per console line

// Survive script reloads: only the newest copy keeps its schedule alive.
var MinervaGeneration = (typeof MinervaGeneration === 'number' ? MinervaGeneration : 0) + 1;

(function () {
	var generation = MinervaGeneration;
	var seq = 0;
	var outbox = []; // console lines not yet written
	var outboxHead = 0;
	var sigs = null; // key -> row JSON as last reported
	var kids = null; // key -> child names as last walked
	var walks = 0;
	var lastFullMs = 0;
	var walk = null; // walk in progress
	var nextWalkMs = 0;
	var recentBusy = []; // busy ms of the last MINERVA_COMPLETE_EVERY walks
	var frameMaxMs = 0; // longest frame spent in this script since the last message
	var frames = 0;

	function ascii(text) {
		// Escape non-ASCII so console encoding can never corrupt the payload.
		return text.replace(/[\u007f-￿]/g, function (c) {
			return '\\u' + ('0000' + c.charCodeAt(0).toString(16)).slice(-4);
		});
	}

	// Queue a message. Large arrays arrive as pre-serialized parts so no single frame stringifies the whole tree.
	function send(message, field, parts) {
		message.seq = ++seq;
		message.ms = Date.now();
		message.frameMaxMs = frameMaxMs;
		message.frames = frames;
		frameMaxMs = 0;
		frames = 0;
		var json = ascii(JSON.stringify(message));
		if (field) json = json.substring(0, json.length - 1) + ',"' + field + '":[' + parts.join(',') + ']}';
		var count = Math.max(1, Math.ceil(json.length / MINERVA_CHUNK));
		for (var i = 0; i < count; i++) {
			outbox.push('MNRV' + MINERVA_PROTOCOL + '|' + seq + '|' + i + '|' + count + '|' + json.substr(i * MINERVA_CHUNK, MINERVA_CHUNK));
		}
	}

	// Console output is slow (~0.1 ms per line), so it shares the frame budget.
	function flush(deadline) {
		while (outboxHead < outbox.length) {
			$.Msg(outbox[outboxHead++]);
			if (Date.now() >= deadline) break;
		}
		if (outboxHead >= outbox.length) {
			outbox = [];
			outboxHead = 0;
		}
	}

	function read(panel, name) {
		try {
			var v = panel[name];
			return v === undefined ? null : v;
		} catch (e) {
			return null;
		}
	}

	// Layout values are FLT_MAX until a panel has been laid out.
	function coord(v) {
		return typeof v === 'number' && isFinite(v) && Math.abs(v) < 1e7 ? Math.round(v) : null;
	}

	function trim(row) {
		while (row.length > 0 && row[row.length - 1] === null) row.pop();
		return row;
	}

	function topPanel() {
		var p = $.GetContextPanel();
		while (p && p.GetParent && p.GetParent()) p = p.GetParent();
		return p;
	}

	function startWalk(now) {
		var full = sigs === null || now - lastFullMs >= MINERVA_FULL_INTERVAL_MS;
		if (full) {
			sigs = {};
			kids = {};
		}
		var top = topPanel();
		walk = {
			full: full,
			complete: full || walks % MINERVA_COMPLETE_EVERY === 0,
			stack: [{ panel: top, key: '', name: '', parent: -1, type: read(top, 'paneltype'), shown: true }],
			count: 0,
			parts: [], // full: every row; delta: changed rows
			del: [],
			types: [],
			typeIndex: {},
			skipped: 0,
			startedMs: now,
			busyMs: 0,
			truncated: false
		};
	}

	// Forget a removed panel and everything below it.
	function drop(key) {
		var names = kids[key];
		if (names) for (var i = 0; i < names.length; i++) drop(key + '/' + names[i]);
		delete kids[key];
		delete sigs[key];
	}

	// Visit one panel: report it if it changed, detect removed children, queue its children. Keys are id paths;
	// siblings without an id, or with an id already used by an earlier sibling, get "<id or type>#<index>".
	function visit(item) {
		var w = walk;
		var panel = item.panel;
		if (!panel || (panel.IsValid && !panel.IsValid())) return;
		var key = item.key;
		var visible = !!read(panel, 'visible');
		var shown = item.shown && visible;
		var text = read(panel, 'text');
		var value = read(panel, 'value');
		var row = [item.type, visible ? 1 : 0];
		if (shown) {
			row.push(coord(read(panel, 'actualxoffset')), coord(read(panel, 'actualyoffset')),
				coord(read(panel, 'actuallayoutwidth')), coord(read(panel, 'actuallayoutheight')));
		} else {
			row.push(null, null, null, null);
		}
		row.push(typeof text === 'string' && text.length > 0 ? text : null);
		row.push(typeof value === 'number' && isFinite(value) ? value : null);
		trim(row);

		var json = ascii(JSON.stringify(row));
		var index = w.count++;
		if (w.full) {
			var t = item.type === null ? null : String(item.type);
			var ti = null;
			if (t !== null) {
				ti = w.typeIndex[t];
				if (ti === undefined) {
					ti = w.types.length;
					w.typeIndex[t] = ti;
					w.types.push(t);
				}
			}
			w.parts.push(ascii(JSON.stringify(trim([item.parent, item.name, ti].concat(row.slice(1))))));
		} else if (sigs[key] !== json) {
			w.parts.push('[' + ascii(JSON.stringify(key)) + ',' + json.substring(1));
		}
		sigs[key] = json;

		if (!visible && !w.complete) {
			w.skipped++;
			return;
		}

		var n = panel.GetChildCount ? panel.GetChildCount() : 0;
		var names = [];
		var children = [];
		var used = {};
		for (var i = 0; i < n; i++) {
			var child = panel.GetChild(i);
			if (!child) continue;
			var id = read(child, 'id');
			var type = read(child, 'paneltype');
			var name = id ? id : (type || 'Panel');
			if (!id || used[name] === true) name = name + '#' + i;
			used[name] = true;
			names.push(name);
			children.push({ panel: child, key: key + '/' + name, name: name, parent: index, type: type, shown: shown });
		}
		var before = kids[key];
		if (before !== undefined) {
			for (var b = 0; b < before.length; b++) {
				if (used[before[b]] !== true) {
					w.del.push(key + '/' + before[b]);
					drop(key + '/' + before[b]);
				}
			}
		}
		kids[key] = names;
		for (var j = children.length - 1; j >= 0; j--) w.stack.push(children[j]);
	}

	function finishWalk(now) {
		var w = walk;
		walk = null;
		walks++;
		var m = {
			t: w.full ? 'full' : 'delta',
			n: w.count,
			complete: w.complete,
			skipped: w.skipped,
			truncated: w.truncated,
			walkMs: now - w.startedMs,
			busyMs: w.busyMs
		};
		if (w.full) {
			m.types = w.types;
			lastFullMs = now;
			send(m, 'panels', w.parts);
		} else {
			m.del = w.del;
			send(m, 'set', w.parts);
		}
		// Space walks so their average cost stays under MINERVA_MAX_LOAD of the time. Averaging over a cycle of walks
		// keeps one expensive complete walk from delaying the next cheap one.
		recentBusy.push(w.busyMs);
		if (recentBusy.length > MINERVA_COMPLETE_EVERY) recentBusy.shift();
		var total = 0;
		for (var i = 0; i < recentBusy.length; i++) total += recentBusy[i];
		nextWalkMs = w.startedMs + Math.max(MINERVA_MIN_INTERVAL_MS, total / recentBusy.length / MINERVA_MAX_LOAD);
	}

	function frame() {
		if (generation !== MinervaGeneration) return; // a newer copy of this script took over
		var start = Date.now();
		var deadline = start + MINERVA_SLICE_MS;
		try {
			if (outboxHead < outbox.length) flush(deadline);
			if (walk === null && outboxHead >= outbox.length && start >= nextWalkMs) startWalk(start);
			if (walk !== null) {
				var k = 0;
				while (walk.stack.length > 0) {
					if (walk.count >= MINERVA_MAX_PANELS) {
						walk.truncated = true;
						break;
					}
					visit(walk.stack.pop());
					if ((++k & 3) === 0 && Date.now() >= deadline) break;
				}
				var end = Date.now();
				walk.busyMs += end - start;
				if (walk.stack.length === 0 || walk.truncated) finishWalk(end);
			}
		} catch (e) {
			// Rows visited so far were recorded but not sent: start over with a full dump.
			walk = null;
			sigs = null;
			kids = null;
			nextWalkMs = start + MINERVA_MIN_INTERVAL_MS;
			send({ t: 'error', where: 'frame', message: String(e) });
		}
		var spent = Date.now() - start;
		if (spent > frameMaxMs) frameMaxMs = spent;
		frames++;

		// Every frame while there is work; otherwise sleep until the next walk is due.
		var idle = walk === null && outboxHead >= outbox.length;
		$.Schedule(idle ? Math.max(0, (nextWalkMs - Date.now()) / 1000) : 0, frame);
	}

	send({
		t: 'hello',
		protocol: MINERVA_PROTOCOL,
		generation: generation,
		context: read($.GetContextPanel(), 'id'),
		top: read(topPanel(), 'id')
	});
	$.Schedule(0, frame);
})();
