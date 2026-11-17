// Builds the StripDeathroll14 mock-up scenes from the window code (MainWindow.cs, MainWindow.Circle.cs,
// WorldMarkers.cs). Every screenshot page and the walkthrough call SDR.mount(name, host) so both show the same thing.
// One game across the scenes: seats 1-4 = Wol Adventurer (you, the host), Wol Companion, Wol Wanderer, Wol Traveller.
(() => {
  const C = {
    text: "#dde2f1", faint: "#bec6dc", button: "#653c39", frame: "#303540", primary: "#ffb3ad", tertiary: "#e8c08d",
    onSurface: "#dde2f1", outline: "#8891a5",
    // Dalamud ImGuiColors the window uses for meaning
    green: "#00cc22", yellow: "#ffff66", red: "#ff0000", blue: "#0099ff", grey: "#b3b3b3", orange: "#ffb500",
  };
  const esc = (s) => String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
  const ICON = { check: "\u2713", redo: "\u21bb", play: "\u25b6", pause: "\u275a\u275a", dice: "\u2684", horn: "\u25c4", broom: "\u2738", plus: "+", cross: "\u2316", users: "\u2687", plane: "\u27a4", times: "\u2715", undo: "\u21b6" };

  const btn = (label, kind, icon, off) => `<span class="mb ${kind}${off ? " disabled" : ""}">${icon ? `<span class="ic">${ICON[icon]}</span>` : ""}${esc(label)}</span>`;
  const sw = (label, on, off) => `<span class="sw ${on ? "on" : "off"}${off ? " disabled" : ""}"><span class="track"><span class="thumb"></span></span>${esc(label)}</span>`;
  const chip = (label, on) => `<span class="chip${on ? " on" : ""}">${on ? "\u2713 " : ""}${esc(label)}</span>`;
  const combo = (value, label, w, popup) => `<span class="frame combo" style="width:${w}px">${esc(value)}</span>${label ? `<span>${esc(label)}</span>` : ""}${popup || ""}`;
  const dis = (s) => `<div class="t dis">${esc(s)}</div>`;
  const t = (s, cls) => `<div class="t${cls ? " " + cls : ""}">${esc(s)}</div>`;
  const row = (...xs) => `<div class="row">${xs.join("")}</div>`;
  const help = `<span class="help">(?)</span>`;
  const badge = (s, c) => `<span class="badge" style="background:${c}">${esc(s)}</span>`;
  const card = (edge, inner) => `<div class="card" style="--edge:${edge};border-color:${edge}73">${inner}</div>`;
  const callout = (s, c) => `<div class="kcallout" style="border-color:${c};color:${c}">${esc(s)}</div>`;
  const section = (title, num) => `<div class="sec"><span class="dia"></span>${num ? `<span class="num">${num}</span><span class="slash"> / </span>` : ""}<span class="title">${esc(title.toUpperCase())}</span><span class="rule"></span></div>`;
  const tabs = (on) => `<div class="tabs">${["Game", "History", "Messages", "Settings"].map((n) => `<span class="tab${n === on ? " on" : ""}">${n}</span>`).join("")}</div>`;
  const drag = (lo, hi, label) => `<span class="frame center" style="width:56px">${lo} s</span><span class="frame center" style="width:56px;margin-left:-4px">${hi} s</span><span>${label}</span>`;
  const slider = (v, min, max, w, label) => {
    const x = 2 + ((v - min) / (max - min)) * (w - 16); // grab position from the value
    return `<span class="frame center" style="width:${w}px"><span class="grab" style="left:${x}px"></span><span>${v}</span></span><span>${esc(label)}</span>`;
  };
  const input = (value, hint, w) => `<span class="frame" style="width:${w}px${value ? "" : ";color:#bec6dcc7"}">${esc(value || hint || "")}</span>`;

  const win = (tab, body, extra) => `<div class="mk-window" style="top:56px;left:588px;width:680px;height:620px;${extra || ""}">` +
    `<div class="mk-title"><span class="tri">\u25bc</span><span>Strip Deathroll</span><span class="x">\u2715</span></div>` +
    `<div class="mk-body">${tabs(tab)}${body}</div></div>`;

  const PLAYERS = ["Wol Adventurer", "Wol Companion", "Wol Wanderer", "Wol Traveller"];
  const venueRow = (venue, calls, popup) => row(combo(venue === "private" ? "Private room (yell)" : "Public (party/alliance)", "Venue", 200, popup), combo(calls, "Calls in", 150));
  const autoRow = (venue) => row(sw("Auto-post", false), drag(2, 4, "between lines"),
    `<span class="dis">${venue === "private" ? "Calls go to /y; players roll /random [N]." : "Calls go to /p; players roll /dice party [N]."}</span>`);
  const actionRow = (phase, myRoll, canCall) => row(
    phase === "Setup" ? btn("Start round", "filled", "play") : btn("Restart round", "outlined", "redo", phase === "Pick"),
    btn(myRoll ? `Roll for me: ${myRoll}` : "Roll for me", "filled", "dice", !myRoll),
    btn("Call again", "tonal", "horn", !canCall),
    btn("New game", "text", "broom"));
  const proposed = (prefix, lines) => card(C.blue, t(`To post in ${prefix}:`) + lines.map((l) => `<div class="t">\u2022  ${esc(l)}</div>`).join("") +
    row(btn("Post", "filled", "plane"), btn("Discard", "text", "times")));
  const addRow = (note) => row(input("", "Forename Surname", 180), btn("Add", "tonal", "plus"), btn("Add target", "tonal", "cross"), btn("Add party/alliance", "tonal", "users")) +
    (note ? dis(note) : "") + dis("Clockwise from the top. Drag a seat to move it; right-click for more.");

  // ---- the circle (MainWindow.Circle.cs DrawCircle), drawn into an SVG once the space it gets is known ----
  const measure = (() => { const c = document.createElement("canvas").getContext("2d"); c.font = '13px "Segoe UI", system-ui, sans-serif'; return (s) => c.measureText(s).width; })();
  const circleSlot = (g) => `<div class="circle" data-g='${JSON.stringify(g).replace(/'/g, "&#39;")}'></div>`;

  function drawCircle(el) {
    const g = JSON.parse(el.dataset.g);
    const body = el.closest(".mk-body");
    const line = 17, W = el.getBoundingClientRect().width;
    const availY = body.getBoundingClientRect().bottom - 8 - el.getBoundingClientRect().top;
    const height = Math.max(240, Math.min(W, availY - 4));
    const n = g.players.length;
    const cx = W / 2, cy = height / 2;
    const labelW = Math.max(...g.players.map((p) => measure(p.name)));
    const node = Math.min(20, Math.max(11, 160 / n));
    const ring = Math.max(40, Math.min(W / 2 - labelW - node - 10, height / 2 - node - 2 * line - 8));
    const dir = (i, k = 0) => { const a = -Math.PI / 2 + 2 * Math.PI * (i + k) / n; return [Math.cos(a), Math.sin(a)]; };
    const seat = (i) => { const [x, y] = dir(i); return [cx + x * ring, cy + y * ring]; };
    const tri = (tip, d, size, col) => { const s = [-d[1] * size * 0.6, d[0] * size * 0.6]; const b = [tip[0] - d[0] * size, tip[1] - d[1] * size];
      return `<polygon points="${tip} ${b[0] + s[0]},${b[1] + s[1]} ${b[0] - s[0]},${b[1] - s[1]}" fill="${col}"/>`; };
    const txt = (x, y, s, col, anchor) => `<text x="${x}" y="${y}" fill="${col}" dominant-baseline="text-before-edge"${anchor ? ` text-anchor="${anchor}"` : ""}>${esc(s)}</text>`;
    const idx = (name) => g.players.findIndex((p) => p.name === name);
    const cur = idx(g.current), next = idx(g.next), starter = idx(g.starter);
    let svg = `<circle cx="${cx}" cy="${cy}" r="${ring}" fill="none" stroke="${C.faint}" stroke-width="1.5"/>`;
    for (let i = 0; i < n; i++) { const [x, y] = dir(i, 0.5); svg += tri([cx + x * ring, cy + y * ring], [-y, x], 5, C.faint); }
    if (cur >= 0 && next >= 0) {
      const a = seat(cur), b = seat(next), len = Math.hypot(b[0] - a[0], b[1] - a[1]), d = [(b[0] - a[0]) / len, (b[1] - a[1]) / len];
      const tip = [b[0] - d[0] * (node + 3), b[1] - d[1] * (node + 3)];
      svg += `<line x1="${a[0] + d[0] * (node + 3)}" y1="${a[1] + d[1] * (node + 3)}" x2="${tip[0]}" y2="${tip[1]}" stroke="${C.green}" stroke-width="2.5"/>` + tri(tip, d, 8, C.green);
    }
    g.players.forEach((p, i) => {
      const [x, y] = seat(i), isCur = i === cur, isStart = i === starter, out = p.lives === 0;
      if (isCur) svg += `<circle class="pulse" data-node="${node}" cx="${x}" cy="${y}" r="${node + 4}" fill="none" stroke="${C.green}" stroke-width="2"/>`;
      svg += `<circle cx="${x}" cy="${y}" r="${node}" fill="${out ? C.frame : isCur ? C.green : isStart ? C.yellow : C.button}"/>`;
      if (i === next) svg += `<circle cx="${x}" cy="${y}" r="${node}" fill="none" stroke="${C.green}" stroke-width="2"/>`;
      svg += txt(x, y - line / 2, i + 1, isCur || isStart ? "#000" : out ? C.faint : C.text, "middle");
      const [dx, dy] = dir(i), name = p.name, dot = 3, dotsW = out ? 0 : 3 * 3 * dot;
      const sub = out ? "out" : p.last != null ? String(p.last) : "";
      const w = Math.max(measure(name), dotsW + measure(sub));
      const ax = cx + dx * (ring + node + 6), ay = cy + dy * (ring + node + 6);
      const lx = dx > 0.3 ? ax : dx < -0.3 ? ax - w : ax - w / 2;
      const ly = dy < -0.3 ? ay - 2 * line : dy > 0.3 ? ay : ay - line;
      const col = out ? C.faint : isCur ? C.green : isStart ? C.yellow : C.text;
      svg += txt(lx, ly, name, col);
      if (!out) for (let k = 0; k < 3; k++) {
        const px = lx + dot + k * 3 * dot, py = ly + line * 1.5;
        svg += k < p.lives ? `<circle cx="${px}" cy="${py}" r="${dot}" fill="${col}"/>` : `<circle cx="${px}" cy="${py}" r="${dot}" fill="none" stroke="${C.faint}" stroke-width="1"/>`;
      }
      svg += txt(lx + dotsW, ly + line, sub, C.faint);
    });
    const mid = [[g.round > 0 ? `Round ${g.round}` : "Not started", C.text]];
    const first = (s) => s.split(" ")[0];
    if (g.phase === "Turns" && g.current) mid.push([`${first(g.current)} to roll`, C.green]);
    if (g.phase === "Turns") mid.push([g.limit != null ? `up to ${g.limit}` : "fresh roll", C.faint]);
    else if (g.phase === "Pick") mid.push([`${first(g.winner)} won`, C.yellow]);
    let top = cy - mid.length * line / 2;
    for (const [s, col] of mid) { svg += txt(cx, top, s, col, "middle"); top += line; }
    el.innerHTML = `<svg width="${W}" height="${height}" style="display:block;font:13px 'Segoe UI',system-ui,sans-serif">${svg}</svg>`;
  }

  // ---- world markers stand-in (WorldMarkers.cs Build): rings, pillars of light and labels in the dark KosMos scheme ----
  function world(x, y, w, h, marks) {
    const grad = (id, col) => `<linearGradient id="${id}" x1="0" y1="1" x2="0" y2="0"><stop offset="0" stop-color="${col}" stop-opacity="1"/><stop offset="1" stop-color="${col}" stop-opacity="0"/></linearGradient>`;
    let s = `<div class="world" style="left:${x}px;top:${y}px;width:${w}px;height:${h}px"><div class="note">In the game world (illustrated stand-in for what KosDraw draws)</div><svg width="${w}" height="${h}" style="position:absolute;inset:0;font:13px 'Segoe UI',sans-serif"><defs>` +
      marks.map((m, i) => grad(`pl${i}`, m.color)).join("") + `</defs>`;
    marks.forEach((m, i) => {
      const rx = 44, ry = 14;
      if (m.pillar) { const ph = m.tall ? 250 : 140; // Beacon: a soft wide glow round a brighter core
        s += `<rect x="${m.x - rx * 0.85}" y="${m.y - ph}" width="${rx * 1.7}" height="${ph}" fill="url(#pl${i})" opacity="0.22"/><rect x="${m.x - 9}" y="${m.y - ph}" width="18" height="${ph}" fill="url(#pl${i})" opacity="0.55"/>`; }
      s += `<ellipse cx="${m.x}" cy="${m.y}" rx="${rx}" ry="${ry}" fill="none" stroke="${m.color}" stroke-opacity="${m.alpha || 0.9}" stroke-width="${m.thin ? 2 : 5}"/>`;
      if (m.gleam) s += `<ellipse class="gleam" cx="${m.x}" cy="${m.y}" rx="${rx + 6}" ry="${ry + 3}" fill="none" stroke="${C.onSurface}" stroke-width="3" pathLength="100" stroke-dasharray="14 86" stroke-dashoffset="0"/>`;
      if (m.ripple) s += `<ellipse class="ripple" cx="${m.x}" cy="${m.y}" rx="${rx * 1.5}" ry="${ry * 1.5}" fill="none" stroke="${m.ripple}" stroke-width="2" stroke-opacity="0.55"/>`;
      s += `<ellipse cx="${m.x}" cy="${m.y - 34}" rx="9" ry="30" fill="#1a1f29" opacity="0.9"/><circle cx="${m.x}" cy="${m.y - 72}" r="8" fill="#1a1f29" opacity="0.9"/>`;
      if (m.label) s += `<text x="${m.x}" y="${m.y - 92}" fill="${m.color}" text-anchor="middle" font-weight="600">${esc(m.label)}</text>`;
      s += `<text x="${m.x}" y="${m.y + 36}" fill="#cfd5e2" text-anchor="middle" font-size="12">${esc(m.name)}</text>`;
    });
    if (marks.arch) { const [a, b] = marks.arch, d = `M ${a.x} ${a.y - 4} Q ${(a.x + b.x) / 2} ${Math.min(a.y, b.y) - 150} ${b.x} ${b.y - 4}`;
      s += `<path d="${d}" fill="none" stroke="${C.primary}" stroke-opacity="0.18" stroke-width="2"/>` +
        `<path class="arch" d="${d}" fill="none" stroke="${C.primary}" stroke-opacity="0.95" stroke-width="4" stroke-linecap="round" pathLength="100" stroke-dasharray="16 84" stroke-dashoffset="-55"/>`; }
    return s + `</svg></div>`;
  }

  const chat = (lines) => `<div class="chat-panel" style="width:540px">${lines.map(([cls, s]) => `<div class="chat-line ${cls}">${esc(s)}</div>`).join("")}</div>`;
  const P = (name, lives, last) => ({ name, lives, last });

  // ---- scenes ----
  const S = {};

  // 01: setup, Private room (the default venue), four players in, the Venue dropdown open.
  S.setup = () => win("Game",
    venueRow("private", "Venue default", `<div class="popup" style="left:0;top:24px;width:200px"><div class="sel">Private room (yell)</div><div>Public (party/alliance)</div></div>`) +
    autoRow("private") +
    card(C.grey, row(badge("Setup", C.grey), "<span>Add at least 2 players, then Start round.</span>")) +
    actionRow("Setup", null, false) + section("Players") + addRow("Added 4 player(s).") +
    circleSlot({ round: 0, phase: "Setup", players: PLAYERS.map((n) => P(n, 3, null)) }));

  // 02: Public, round 1. Opening rolls 640 / 233 / 95 / 871: Traveller (highest) started with a fresh /dice party and
  // rolled 412, so it is your turn to /dice party 412; Companion is next. Reminder on, counting down.
  S.turns = () => win("Game",
    venueRow("public", "Venue default") + autoRow("public") +
    card(C.green, row(badge("Turns", C.green), "<span>Round 1: next to roll is Wol Adventurer - /dice party 412.</span>",
      `<span class="dis">Reminder in 18 s (3 left).</span>`, btn("Pause", "text", "pause")) +
      t("Highest opening: Wol Traveller (871).", "yellow")) +
    actionRow("Turns", "/dice party 412", true) +
    proposed("/p", ["Wol Adventurer, roll /dice party 412"]) +
    section("Players") + addRow("") +
    circleSlot({ round: 1, phase: "Turns", current: "Wol Adventurer", next: "Wol Companion", starter: "Wol Traveller", limit: 412,
      players: [P("Wol Adventurer", 3, 640), P("Wol Companion", 3, 233), P("Wol Wanderer", 3, 95), P("Wol Traveller", 3, 412)] }));

  // 03: Private room, round 2. Traveller won round 1 (Wanderer lost their top), so Adventurer started round 2 with
  // a fresh /random and rolled 532; Companion then rolled a bare /random: 57 out of 999, a wrong number.
  const wrongAlert = "Wol Companion rolled 57 out of 999, expected /random 532. Accept it or ask for a reroll.";
  S.wrong = () => win("Game",
    venueRow("private", "Venue default") + autoRow("private") +
    card(C.green, row(badge("Turns", C.green), "<span>Round 2: next to roll is Wol Companion - /random 532.</span>") +
      t("Started by Wol Adventurer (next after last round's winner).", "yellow") + callout(wrongAlert, C.red) +
      row(btn("Accept 57", "filled", "check"), btn("Ask for reroll", "tonal", "redo"))) +
    actionRow("Turns", null, true) + section("Players") + addRow("") +
    circleSlot({ round: 2, phase: "Turns", current: "Wol Companion", next: "Wol Wanderer", starter: "Wol Adventurer", limit: 532,
      players: [P("Wol Adventurer", 3, 532), P("Wol Companion", 3, 57), P("Wol Wanderer", 2, 12), P("Wol Traveller", 3, 1)] }));

  // 04: Private room, round 2: Companion rerolled 205, Wanderer rolled 69 and won; Wanderer picks Companion's top.
  S.pick = () => win("Game",
    venueRow("private", "Venue default") + autoRow("private") +
    card(C.yellow, row(badge("Pick", C.yellow), "<span>Round 2: Wol Wanderer rolled 69 and wins. Who strips, and what?</span>") +
      row(combo("Wol Companion", "Who strips?", 220), chip("Top", true), chip("Bottom", false), chip("Shoes", false), btn("Confirm", "filled", "check"))) +
    actionRow("Pick", null, true) +
    proposed("/y", ["Wol Wanderer rolled 69 and wins round 2! Wol Wanderer, who strips?"]) +
    section("Players") + addRow("") +
    circleSlot({ round: 2, phase: "Pick", current: "Wol Wanderer", next: "Wol Traveller", starter: "Wol Adventurer", winner: "Wol Wanderer",
      players: [P("Wol Adventurer", 3, 532), P("Wol Companion", 3, 205), P("Wol Wanderer", 2, 69), P("Wol Traveller", 3, 1)] }));

  // 05: History: round 3 in play (opened by default), round 2 opened by hand, round 1 closed. Same game as 02-04.
  const hdr = (open, s) => `<div class="chdr"><span class="arr">${open ? "\u25bc" : "\u25b6"}</span><span>${esc(s)}</span></div>`;
  const NOTE = { opening: ["opening", "dis"], wrong: ["wrong number (not counted unless accepted)", "orange"], won: ["winning roll", "green"], "": ["", ""] };
  const rolls = (rows) => `<table class="rolls"><tr><th style="width:45%">Player</th><th style="width:40px">Roll</th><th style="width:50px">Out of</th><th>Note</th></tr>` +
    rows.map(([p, v, m, n]) => `<tr><td>${p}</td><td>${v}</td><td>${m}</td><td class="${NOTE[n][1]}">${NOTE[n][0]}</td></tr>`).join("") + `</table>`;
  S.history = () => win("History",
    hdr(true, "Round 3 (in play)") + rolls([["Wol Traveller", 777, 999, ""], ["Wol Adventurer", 450, 777, ""]]) +
    hdr(true, "Round 2: Wol Wanderer won with 69; Wol Companion lost their top (2 left)") +
    rolls([["Wol Adventurer", 532, 999, ""], ["Wol Companion", 57, 999, "wrong"], ["Wol Companion", 205, 532, ""], ["Wol Wanderer", 69, 205, "won"]]) +
    hdr(false, "Round 1: Wol Traveller won with 1; Wol Wanderer lost their top (2 left)"));

  // 06: Messages: rules and ad, then the game lines; the Call line is reworded by the host.
  const RULES = "Strip Deathroll! Everyone rolls /random or /dice to start; highest goes first, then clockwise.\nOn your turn, roll /random or /dice up to the number the player before you rolled.\nRoll a 0, 1, 69 or 666 to win the round - then you pick who strips.\nEveryone has 3 pieces. Lose all 3 and you are out!";
  const msg = (label, keys, value, example) => row(`<span>${esc(label)}</span>`, `<span class="dis">${esc(keys)}</span>`) +
    row(input(value, "", 560), btn("Default", "text", "undo", !value)) + dis(`e.g. ${example}`);
  S.messages = () => win("Messages",
    row(combo("Say", "Post to", 160)) + dis("One chat line per text line, up to 10, 2-4 s apart.") +
    section("Rules") + `<div class="multi">${esc(RULES)}</div>` + row(btn("Post rules", "tonal", "horn"), btn("Default", "text", "undo")) +
    section("Ad") + `<div class="multi" style="height:44px">Strip Deathroll starting soon! Ask to join the game.</div>` + row(btn("Post ad", "tonal", "horn"), btn("Default", "text", "undo")) +
    section("Game lines") + dis("What the plugin posts during a game. Words in {braces} are filled in; a blank line uses the default.") +
    msg("Round start", "{round} {roll}", "", "Strip Deathroll round 3! Everyone roll /random - highest goes first.") +
    msg("Call", "{name} {roll}", "{name}, you're up - {roll}", "Ann Lee, you're up - /random 412") +
    msg("Win", "{name} {number} {round}", "", "Ann Lee rolled 69 and wins round 3! Ann Lee, who strips?"));
  S.turnsChat = () => chat([["party", "(Wol Adventurer) Strip Deathroll round 1! Everyone roll /dice party - highest goes first."],
    ["party", "(Wol Adventurer) Random! 640"], ["party", "(Wol Companion) Random! 233"], ["party", "(Wol Wanderer) Random! 95"],
    ["party", "(Wol Traveller) Random! 871"], ["party", "(Wol Adventurer) Wol Traveller, roll /dice party"], ["party", "(Wol Traveller) Random! 412"]]);
  S.wrongChat = () => chat([["yell", "Wol Adventurer: Wol Traveller rolled 1 and wins round 1!"], ["yell", "Wol Adventurer: Wol Wanderer loses their top! 2 pieces left."],
    ["yell", "Wol Adventurer: Wol Adventurer, roll /random"], ["random", "Random! You roll a 532."], ["yell", "Wol Adventurer: Wol Companion, roll /random 532"],
    ["random", "Random! Wol Companion rolls a 57."], ["echo", `[Deathroll] ${wrongAlert}`]]);
  S.pickChat = () => chat([["yell", "Wol Adventurer: Wol Companion, roll /random 532"], ["random", "Random! Wol Companion rolls a 205 (out of 532)."],
    ["yell", "Wol Adventurer: Wol Wanderer, roll /random 205"], ["random", "Random! Wol Wanderer rolls a 69 (out of 205)."]]);
  S.messagesChat = () => chat(RULES.split("\n").map((l) => ["say", `Wol Adventurer: ${l}`]));

  // 07: Settings, with the world markers of scene 02's state (you up for 412, Companion next, Traveller started).
  S.settings = () => win("Settings",
    section("Reminder", "01") + row(sw("Remind whoever has not rolled", true)) + row(slider(30, 15, 120, 200, "Seconds of quiet")) +
    row(sw("Only remind me (Echo, sends nothing)", false)) + row(combo("Say", "Remind in", 120)) +
    dis("Posts the reminder line (Messages tab) after that long with no roll, at most 3 times per turn,\nwhether or not the call was posted. Not while a wrong roll waits for you. Pause it from the Game tab.") +
    section("World markers", "02") + row(sw("Mark the players in the world", true)) +
    row(`<span class="seg"><span class="on">ImGui</span><span>GPU</span></span>`, help) + row(sw("Animate the markers", true), help) +
    row(sw("Move the rings", false), btn("Rings follow the players", "text", "undo", true)) +
    dis("While on (and this window is open), drag the handle in a ring to put it where you want; right-click a\nhandle to let that ring follow its player again. Placed rings stay put until you change zone.") +
    dis("Pillars of light: crimson, tall, with the number to roll: whoever is up. White: next. Gold, tall: won the round.\nGold, short: started the turns. Gold ring: owes an opening roll. Thin ring: everyone else. Out: nothing.") +
    dis("4 of 4 players found nearby.") +
    section("Window", "03") + row(sw("Frosted glass", false), help) +
    section("Debug", "04") + row(sw("Debug mode (this session only)", false)) +
    dis("Adds an Add dummy button; right-click a dummy's seat to make it roll, so you can play a whole game alone.\nWhile it is on, calls, results, reminders and the rules/ad print to Echo instead of chat; Roll for me still rolls.\nTurning it off removes the dummies."),
    "font-size:12.5px");
  S.world = () => {
    const m = [
      { name: "Wol Adventurer", x: 130, y: 480, color: C.primary, pillar: true, tall: true, gleam: true, label: "Roll 412" },
      { name: "Wol Companion", x: 330, y: 515, color: C.onSurface, pillar: true, alpha: 0.8, label: "Next" },
      { name: "Wol Wanderer", x: 250, y: 360, color: C.outline, thin: true, alpha: 0.6 },
      { name: "Wol Traveller", x: 440, y: 380, color: C.tertiary, pillar: true, alpha: 0.7, label: "Started", ripple: C.primary },
    ];
    m.arch = [m[3], m[0]]; // the hand-over pulse from Traveller (who just rolled 412) to you
    return world(24, 60, 540, 560, m);
  };

  window.SDR = {
    build: (name) => S[name](),
    // host: element to fill; circles are laid out once the window is in the page, like ImGui's avail region
    mount(names, host = document.body) {
      const wrap = document.createElement("div"); // static strings built above, no outside input
      wrap.innerHTML = names.map((n) => S[n]()).join("");
      while (wrap.firstChild) host.appendChild(wrap.firstChild);
      host.querySelectorAll(".circle").forEach(drawCircle);
    },
    // walkthrough motion: the seat pulse (MainWindow.Circle.cs: node + 4 + 2 sin(4t)), the gleam (2.5 s lap),
    // ripples (1.2 s) and the hand-over pulse (0.9 s) from WorldMarkers.cs
    animate(root, t) {
      root.querySelectorAll(".pulse").forEach((c) => c.setAttribute("r", +c.dataset.node + 4 + 2 * Math.sin(t * 4)));
      root.querySelectorAll(".gleam").forEach((e) => e.setAttribute("stroke-dashoffset", -((t % 2.5) / 2.5) * 100));
      root.querySelectorAll(".ripple").forEach((e) => { const u = (t % 1.2) / 1.2; e.setAttribute("rx", 44 * (1 + 1.5 * u)); e.setAttribute("ry", 14 * (1 + 1.5 * u)); e.setAttribute("stroke-opacity", 0.9 * (1 - u)); });
      root.querySelectorAll(".arch").forEach((e) => { const u = (t % 1.8) / 0.9; e.style.opacity = u <= 1 ? 1 : 0; e.setAttribute("stroke-dashoffset", -u * 84); });
    },
  };
})();
