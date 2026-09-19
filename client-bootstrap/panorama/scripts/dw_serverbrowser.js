"use strict";

(function () {

    var BASE_URL = "https://api.deadworks.net";
    var PARALLEL = 4;
    var POLL_STEP = 0.05;
    var POLL_STEP_LOCAL = 0.008;
    var PARALLEL_LOCAL = 8;
    var _pollStep = POLL_STEP;
    var _parallel = PARALLEL;
    var REQ_TIMEOUT_MS = 8000;
    var PROBE_TIMEOUT_MS = 25000;
    var PROBE_ATTEMPTS = 3;
    var MAX_REHEAD = 3;

    var LPORT_BASE = 47800, LPORT_SPAN = 4;
    var LPROTO = 1;
    var LMAGIC = 42;
    var L_TIMEOUT_MS = 5000;
    var L_DISCOVER_MS = 2000;
    var L_WATCHDOG_S = 2.0;
    var L_POLL_S = 0.35;
    var L_DETAIL_EVERY = 4;
    var L_MAX_MISSES = 6;

    var LS_IDLE = 0, LS_QUEUED = 1, LS_RESOLVING = 2, LS_DOWNLOADING = 3,
        LS_INSTALLING = 4, LS_READY = 6, LS_ERROR = 7, LS_CANCELLED = 8;

    var UNIT_BASE = 15, UNIT_STEP = 9, UNIT_BITS = 6, UNITS_PER_IMAGE = 2;
    var PROBE_W = 600, PROBE_H = 800;
    var FORMAT_VERSION = 4;
    var MAP_TABLE = ["", "street_test", "dl_streets", "dl_midtown", "dl_hideout",
                     "1v1_test", "hero_testing", "new_player_basics", "start"];

    var COUNTRY_TABLE = (
        "ad ae af ag ai al am an ao ar as at au aw ax az ba bb bd be bf bg bh bi bj bm bn bo br bs bt bv bw by bz " +
        "ca cc cd cf cg ch ci ck cl cm cn co cr cu cv cx cy cz de dj dk dm do dz ec ee eg eh er es et fi fj fk fm " +
        "fo fr ga gb gd ge gf gg gh gi gl gm gn gp gq gr gs gt gu gw gy hk hm hn hr ht hu id ie il im in io iq ir " +
        "is it je jm jo jp ke kg kh ki km kn kp kr kw ky kz la lb lc li lk lr ls lt lu lv ly ma mc md me mg mh mk " +
        "ml mm mn mo mp mq mr ms mt mu mv mw mx my mz na nc ne nf ng ni nl no np nr nu nz om pa pe pf pg ph pk pl " +
        "pm pn pr ps pt pw py qa re ro rs ru rw sa sb sc sd se sg sh si sj sk sl sm sn so sr st sv sy sz tc td tf " +
        "tg th tj tk tl tm tn to tr tt tv tw tz ua ug um us uy uz va vc ve vg vi vn vu wf ws ye yt za zm zw"
    ).split(" ");

    function countryCode(idx) {
        return (idx >= 1 && idx <= COUNTRY_TABLE.length) ? COUNTRY_TABLE[idx - 1] : "";
    }

    var _held = null;
    var ATTR_VER = "dwb_ver", ATTR_BODY = "dwb_body";
    var HEX = "0123456789abcdef";

    function toHex(bytes) {
        var s = "";
        for (var i = 0; i < bytes.length; i++) s += HEX.charAt((bytes[i] >> 4) & 15) + HEX.charAt(bytes[i] & 15);
        return s;
    }
    function fromHex(s) {
        if (!s || (s.length & 1)) return null;
        var out = [];
        for (var i = 0; i < s.length; i += 2) {
            var v = parseInt(s.substr(i, 2), 16);
            if (isNaN(v)) return null;
            out.push(v);
        }
        return out;
    }

    function saveHeld(version, rowBody, servers) {
        _held = { version: version, rowBody: rowBody, servers: servers };
        var r = windowRoot();
        r.SetAttributeString(ATTR_BODY, toHex(rowBody));
        r.SetAttributeString(ATTR_VER, String(version));
    }

    function loadHeld() {
        var r = windowRoot();
        var v = parseInt(r.GetAttributeString(ATTR_VER, ""), 10);
        if (!isNaN(v)) {
            if (_held && _held.version === v) return _held;
            var body = fromHex(r.GetAttributeString(ATTR_BODY, ""));
            if (body && body.length && fnvVersion(body, body.length) === v) {
                var servers = parseRow(body);
                if (servers) {
                    _held = { version: v, rowBody: body, servers: servers };
                    return _held;
                }
            }
        }
        return _held;
    }

    function fnvVersion(bytes, len) {
        var h = 0x811c9dc5;
        for (var i = 0; i < len; i++) {
            h ^= bytes[i];
            h = (Math.imul(h, 0x01000193)) >>> 0;
        }
        return (h >>> 0) % 256;
    }

    var _reqId = 0;

    function makeHost(parent) {
        var h = $.CreatePanel("Panel", parent, "DWB_NetHost_" + (_reqId++));
        h.style.position = "2px 2px 0px";
        h.style.width = "700px";
        h.style.height = "900px";
        h.style.opacity = "0.02";
        h.style.zIndex = "99999";
        h.hittest = false; h.hittestchildren = false;
        return h;
    }

    function loadImage(host, url, done, fail, timeoutMs) {
        var img = $.CreatePanel("Image", host, "dwbimg_" + (_reqId++));
        img.style.position = "0px 0px 0px";
        img.SetImage(url);

        var elapsed = 0, finished = false;
        function cleanup() {
            img.SetImage("");
            img.DeleteAsync(0);
        }
        $.RegisterEventHandler("ImageFailedLoad", img, function () {
            if (finished) return;
            finished = true;
            cleanup();
            fail("failed");
        });
        function check() {
            if (finished) return;
            var w = Number(img.actuallayoutwidth), hh = Number(img.actuallayoutheight);
            if (w > 0 && hh > 0) {
                finished = true; cleanup(); done(w, hh); return;
            }
            elapsed += _pollStep * 1000;
            if (elapsed >= (timeoutMs || REQ_TIMEOUT_MS)) {
                finished = true; cleanup(); fail("timeout"); return;
            }
            $.Schedule(_pollStep, check);
        }
        $.Schedule(_pollStep, check);
    }

    function cacheBust() { return "rnd=" + Math.random().toString(36).slice(2) + (_reqId); }

    function calibrate(host, done, fail) {
        var reads = [];
        var attempt = 0;
        function retryOrFail(why) {
            attempt++;
            if (attempt < PROBE_ATTEMPTS) {
                reads = [];
                one();
                return;
            }
            if (fail) fail(why);
        }
        function one() {
            loadImage(host, _listBase + "/api/browse/probe.png?" + cacheBust(), function (w, h) {
                reads.push([w, h]);
                if (reads.length < 2) { one(); return; }
                var w0 = (reads[0][0] + reads[1][0]) / 2, h0 = (reads[0][1] + reads[1][1]) / 2;
                var swap = false;
                if (w0 > h0) { swap = true; var t = w0; w0 = h0; h0 = t; }
                var sx = w0 / PROBE_W, sy = h0 / PROBE_H;
                var lo = Math.min(sx, sy), hi = Math.max(sx, sy);
                if (!(lo > 0.05) || (hi - lo) / hi > 0.15) {
                    retryOrFail("probe-distorted");
                    return;
                }
                done({ swap: swap, scaleX: sx, scaleY: sy });
            }, function (r) {  retryOrFail(r); },
               PROBE_TIMEOUT_MS);
        }
        one();
    }

    function decUnit(measured, scale) { return Math.round((measured / scale - UNIT_BASE) / UNIT_STEP); }
    function decPair(mw, mh, cal) {
        var w = mw, h = mh;
        if (cal.swap) { var t = w; w = h; h = t; }
        return [decUnit(w, cal.scaleX), decUnit(h, cal.scaleY)];
    }

    function fetchServerList(host, onDone, onError, progress) {
        var attempts = 0;
        var held = loadHeld();
        var useHave = !!held;

        function haveQuery() {
            return (useHave && held) ? ("&have=" + held.version) : "";
        }

        function run(cal) {
            attempts++;
            var headUnits = [];
            function head(half) {
                loadImage(host, _listBase + "/api/browse/head.png?h=" + half + haveQuery() + "&" + cacheBust(), function (w, h) {
                    var p = decPair(w, h, cal);
                    if (p[0] < 0 || p[1] < 0) { retry("head-sentinel"); return; }
                    headUnits.push(p[0], p[1]);
                    if (headUnits.length < 4) { head(1); return; }
                    var packed = (headUnits[0] << 18) | (headUnits[1] << 12) | (headUnits[2] << 6) | headUnits[3];
                    var version = (packed >> 16) & 0xff;
                    var imageCount = packed & 0xffff;
                    if (imageCount === 0) {
                        if (useHave && held && version === held.version) {
                            onDone(held.servers);
                            return;
                        }
                        retry("unchanged-without-copy");
                        return;
                    }
                    if (imageCount < 1 || imageCount > 4096) { retry("head-range"); return; }
                    fetchData(version, imageCount);
                }, function (r) { retry("head:" + r); });
            }
            head(0);

            function fetchData(version, imageCount) {
                var units = new Array(imageCount * 2);
                var queue = [];
                for (var q = 0; q < imageCount; q++) queue.push(q);
                var doneCount = 0, active = 0, aborted = false, conc = _parallel;
                var retriesLeft = {};

                function startOne(idx) {
                    active++;
                    loadImage(host, _listBase + "/api/browse/d.png?v=" + version + "&i=" + idx + haveQuery() + "&" + cacheBust(),
                        function (w, h) {
                            active--;
                            if (aborted) return;
                            var p = decPair(w, h, cal);
                            if (p[0] < 0 || p[1] < 0) { aborted = true; retry("data-sentinel"); return; }
                            if (p[0] > 63) p[0] = 63;
                            if (p[1] > 63) p[1] = 63;
                            units[idx * 2] = p[0];
                            units[idx * 2 + 1] = p[1];
                            doneCount++;
                            if (progress) progress(doneCount, imageCount);
                            if (doneCount >= imageCount) { assemble(version, units, imageCount); return; }
                            fill();
                        },
                        function () {
                            active--;
                            if (aborted) return;
                            var rl = (retriesLeft[idx] === undefined) ? 3 : retriesLeft[idx];
                            if (rl > 0) {
                                retriesLeft[idx] = rl - 1;
                                if (conc > 1) conc = 1;
                                queue.push(idx);
                                fill();
                            } else {
                                aborted = true;
                                retry("data-timeout");
                            }
                        });
                }
                function fill() {
                    while (!aborted && active < conc && queue.length > 0) startOne(queue.shift());
                }
                fill();
            }
        }

        function assemble(version, units, imageCount) {
            var totalBits = imageCount * UNITS_PER_IMAGE * UNIT_BITS;
            var nbytes = Math.floor(totalBits / 8);
            var bytes = new Array(nbytes);
            for (var bi = 0; bi < nbytes; bi++) {
                var val = 0;
                for (var b = 0; b < 8; b++) {
                    var gb = bi * 8 + b;
                    var ui = Math.floor(gb / UNIT_BITS);
                    var inU = gb % UNIT_BITS;
                    var u = units[ui] || 0;
                    val = (val << 1) | ((u >> (5 - inU)) & 1);
                }
                bytes[bi] = val;
            }
            var parsed = parsePayload(bytes, (useHave && held) ? held.rowBody : null);
            if (!parsed) {  retry("parse"); return; }
            var fnv = fnvVersion(parsed.rowBody, parsed.rowBody.length);
            if (fnv !== version) {  retry("integrity"); return; }
            saveHeld(version, parsed.rowBody, parsed.servers);
            onDone(parsed.servers);
        }

        function retry(reason) {
            if (useHave) {
                useHave = false;
                held = null;
            }
            if (attempts >= MAX_REHEAD) {  if (onError) onError(reason); return; }
            $.Schedule(0.15, function () { run(_cal); });
        }

        if (_cal) run(_cal);
        else calibrate(host, function (c) { _cal = c; run(c); }, function (r) { if (onError) onError("calibrate:" + r); });
    }

    var _cal = null;

    function lzssDecompress(src, dict) {
        var out = [], i = 0, d = 0;
        if (dict) { for (d = 0; d < dict.length; d++) out.push(dict[d]); }
        var dictLen = out.length;
        while (i < src.length) {
            var flags = src[i++];
            for (var b = 0; b < 8 && i < src.length; b++) {
                if (flags & (1 << b)) {
                    if (i + 1 >= src.length) return null;
                    var a = src[i++], c = src[i++];
                    var off = ((a & 0x0f) << 8) | c;
                    var len = (a >> 4) + 3;
                    var st = out.length - off;
                    if (off === 0 || st < 0) return null;
                    for (var k = 0; k < len; k++) out.push(out[st + k]);
                } else {
                    out.push(src[i++]);
                }
            }
        }
        return dictLen ? out.slice(dictLen) : out;
    }

    function mkServer(ip, port, players, max, mapIdx, flags, countryIdx, name) {
        return {
            name: name,
            address: ip + ":" + port,
            players: players,
            max: max,
            map: MAP_TABLE[mapIdx] || "",
            reachable: !!(flags & 1),
            requiresLauncher: !!(flags & 2),
            ping: (flags >> 2) & 7,
            country: countryCode(countryIdx)
        };
    }

    function parseRow(b) {
        if (!b || b.length < 1) return null;
        var count = b[0], o = 1, servers = [];
        for (var i = 0; i < count; i++) {
            if (o + 12 > b.length) return null;
            var ip = b[o] + "." + b[o + 1] + "." + b[o + 2] + "." + b[o + 3];
            var port = (b[o + 4] << 8) | b[o + 5];
            var players = b[o + 6], max = b[o + 7], mapIdx = b[o + 8], fl = b[o + 9], cc = b[o + 10], nl = b[o + 11];
            o += 12;
            if (o + nl > b.length) return null;
            servers.push(mkServer(ip, port, players, max, mapIdx, fl, cc, utf8Decode(b, o, nl)));
            o += nl;
        }
        return servers;
    }

    function columnarToRow(b) {
        if (!b || b.length < 1) return null;
        var count = b[0], o = 1, i, k;
        if (b.length < 1 + count * 12) return null;
        var ips = [], ports = [], players = [], maxes = [], maps = [], flags = [], ccs = [], lens = [];
        for (i = 0; i < count; i++) { ips.push([b[o], b[o + 1], b[o + 2], b[o + 3]]); o += 4; }
        for (i = 0; i < count; i++) { ports.push([b[o], b[o + 1]]); o += 2; }
        for (i = 0; i < count; i++) players.push(b[o++]);
        for (i = 0; i < count; i++) maxes.push(b[o++]);
        for (i = 0; i < count; i++) maps.push(b[o++]);
        for (i = 0; i < count; i++) flags.push(b[o++]);
        for (i = 0; i < count; i++) ccs.push(b[o++]);
        for (i = 0; i < count; i++) lens.push(b[o++]);
        var out = [count];
        for (i = 0; i < count; i++) {
            if (o + lens[i] > b.length) return null;
            out.push(ips[i][0], ips[i][1], ips[i][2], ips[i][3], ports[i][0], ports[i][1],
                     players[i], maxes[i], maps[i], flags[i], ccs[i], lens[i]);
            for (k = 0; k < lens[i]; k++) out.push(b[o + k]);
            o += lens[i];
        }
        return out;
    }

    function parsePayload(bytes, refRowBody) {
        if (!bytes || bytes.length < 4) return null;
        if (bytes[0] !== FORMAT_VERSION) {
            return null;
        }
        var flags = bytes[1];
        var bodyLen = (bytes[2] << 8) | bytes[3];
        var usedLen = 4 + bodyLen;
        if (usedLen > bytes.length) return null;
        var body = bytes.slice(4, usedLen);
        var rowBody;
        if (flags & 4) {
            if (!refRowBody) return null;
            rowBody = lzssDecompress(body, refRowBody);
        } else {
            if (flags & 2) {
                body = lzssDecompress(body);
                if (!body) return null;
            }
            rowBody = (flags & 1) ? columnarToRow(body) : body;
        }
        if (!rowBody) return null;
        var servers = parseRow(rowBody);
        if (!servers) return null;
        return { servers: servers, rowBody: rowBody, usedLen: usedLen };
    }

    function utf8Decode(bytes, start, len) {
        var out = "", i = start, end = start + len;
        while (i < end) {
            var c = bytes[i++];
            if (c < 0x80) { out += String.fromCharCode(c); }
            else if (c < 0xe0) { out += String.fromCharCode(((c & 0x1f) << 6) | (bytes[i++] & 0x3f)); }
            else if (c < 0xf0) { out += String.fromCharCode(((c & 0x0f) << 12) | ((bytes[i++] & 0x3f) << 6) | (bytes[i++] & 0x3f)); }
            else {
                var cp = ((c & 0x07) << 18) | ((bytes[i++] & 0x3f) << 12) | ((bytes[i++] & 0x3f) << 6) | (bytes[i++] & 0x3f);
                cp -= 0x10000;
                out += String.fromCharCode(0xd800 + (cp >> 10), 0xdc00 + (cp & 0x3ff));
            }
        }
        return out;
    }

    function connectTo(server) {
        $.DispatchEvent("CitadelConCommand", "connect " + server.address);
    }

    var _lport = null;
    var _lverified = false;
    var ATTR_LPORT = "dwb_lport";
    var ATTR_SKIP_LAUNCHER = "dwb_skip_launcher";

    function skipLauncherPrompt() {
        return windowRoot().GetAttributeString(ATTR_SKIP_LAUNCHER, "") === "1";
    }
    function setSkipLauncherPrompt() {
        windowRoot().SetAttributeString(ATTR_SKIP_LAUNCHER, "1");
    }

    function launcherUrl(port, route, query) {
        return "http://127.0.0.1:" + port + "/dwl/" + route + ".png?" +
               (query ? query + "&" : "") + cacheBust();
    }

    function saveLauncherPort(port) {
        windowRoot().SetAttributeString(ATTR_LPORT, String(port));
    }
    function cachedLauncherPort() {
        var p = parseInt(windowRoot().GetAttributeString(ATTR_LPORT, ""), 10);
        if (p >= LPORT_BASE && p < LPORT_BASE + LPORT_SPAN) return p;
        return null;
    }

    function findLauncher(host, done, fail) {
        if (_lport !== null) { done(_lport); return; }
        var cached = cachedLauncherPort();
        var ports = [];
        if (cached !== null) ports.push(cached);
        for (var k = 0; k < LPORT_SPAN; k++) {
            var p = LPORT_BASE + k;
            if (p !== cached) ports.push(p);
        }
        var pending = ports.length, settled = false;

        function miss() {
            pending--;
            if (!settled && pending <= 0) fail("not-running");
        }

        function tryPort(port) {
            loadImage(host, launcherUrl(port, "probe"), function (w, h) {
                if (settled) return;
                var sw = w, sh = h, swap = false;
                if (sw > sh) { swap = true; var t = sw; sw = sh; sh = t; }
                var sx = sw / PROBE_W, sy = sh / PROBE_H;
                var lo = Math.min(sx, sy), hi = Math.max(sx, sy);
                if (!(lo > 0.05) || (hi - lo) / hi > 0.15) {
                    miss();
                    return;
                }
                settled = true;
                if (!_cal) {
                    _cal = { swap: swap, scaleX: sx, scaleY: sy };
                }
                _lport = port;
                saveLauncherPort(port);
                done(port);
            }, miss, L_DISCOVER_MS);
        }
        for (var i = 0; i < ports.length; i++) tryPort(ports[i]);
    }

    function forgetLauncher() {
        _lport = null;
        _lverified = false;
        windowRoot().SetAttributeString(ATTR_LPORT, "");
    }

    function launcherHello(host, done, fail) {
        loadImage(host, launcherUrl(_lport, "hello"), function (w, h) {
            var p = decPair(w, h, _cal);
            if (p[1] !== LMAGIC) { fail("not-launcher"); return; }
            if (p[0] !== LPROTO) {
                fail("proto");
                return;
            }
            _lverified = true;
            done();
        }, function () { fail("gone"); }, L_DISCOVER_MS);
    }

    function ensureLauncher(host, done, fail) {
        attempt(false);

        function attempt(rescanned) {
            findLauncher(host, function () {
                launcherHello(host, done, function (r) {
                    if (r === "proto") { fail(r); return; }
                    forgetLauncher();
                    if (rescanned) { fail(r); return; }
                    attempt(true);
                });
            }, fail);
        }
    }

    function launcherPrep(host, address, done, fail) {
        var at = address.lastIndexOf(":");
        var ip = address.slice(0, at), port = address.slice(at + 1);
        loadImage(host, launcherUrl(_lport, "prep", "ip=" + ip + "&port=" + port), function (w, h) {
            var p = decPair(w, h, _cal);
            if (p[0] !== 1) { fail("refused"); return; }
            done(p[1]);
        }, function () {
            forgetLauncher();
            fail("gone");
        }, L_TIMEOUT_MS);
    }

    function launcherCancel(host, seq) {
        if (_lport === null || seq === null) return;
        loadImage(host, launcherUrl(_lport, "cancel", "j=" + seq),
                  function () {  },
                  function () {}, L_TIMEOUT_MS);
    }

    function launcherStatus(host, seq, slot, done, fail) {
        loadImage(host, launcherUrl(_lport, "status", "j=" + seq + "&i=" + slot), function (w, h) {
            done(decPair(w, h, _cal));
        }, fail, L_TIMEOUT_MS);
    }

    var C_TEXT  = "#FFEFD7";
    var C_DIM   = "#FFEFD7aa";
    var C_FAINT = "#FFEFD744";
    var C_BRAND = "#5FE69E";
    var C_GOLD  = "#FFED79";
    var C_PANEL = "rgba(16,19,13,0.97)";
    var C_WELL  = "rgba(0,0,0,0.35)";
    var C_LINE  = "#FFEFD722";
    var C_SCRIM = "rgba(8,10,7,0.88)";
    var F_TITLE = "VALVEOracle, Reaver, sans-serif";
    var F_BODY  = "Retail Demo, Noto Sans, sans-serif";

    var C_BLURPLE = "#5865F2";
    var TITLE_PROGRESS = "CUSTOM CONTENT";
    var TITLE_CONNECTING = "CONNECTING";
    var TITLE_MISSING  = "The Deadworks launcher is not running!";
    var TITLE_OUTDATED = "Your Deadworks launcher is out of date!";

    var PANEL_W = 1180, PANEL_H = 760;
    var COL_FLAG = 34, COL_MAP = 190, COL_PLAYERS = 110, COL_PING = 78, COL_JOIN = 116;

    function mkHeadCell(parent, text, width) {
        var l = $.CreatePanel("Label", parent, "");
        l.text = text;
        l.style.fontSize = "13px";
        l.style.fontFamily = F_TITLE;
        l.style.color = C_FAINT;
        l.style.letterSpacing = "1px";
        l.style.width = width ? (width + "px") : "fill-parent-flow(1.0)";
        if (width === COL_PLAYERS || width === COL_PING) l.style.textAlign = "center";
        return l;
    }

    var _overlay = null, _listPanel = null, _statusLabel = null, _fetchHost = null, _busy = false;
    var _progressTrack = null, _progressFill = null;

    function root() { return $.GetContextPanel(); }
    function windowRoot() {
        var p = root(), best = p, depth = 0;
        while (p && depth < 40) { best = p; p = p.GetParent(); depth++; }
        return best;
    }

    function ensureOverlay() {
        if (_overlay && _overlay.IsValid()) return _overlay;
        var r = windowRoot();
        var existing = null;
        existing = r.FindChildTraverse("DWB_Overlay");
        if (existing) { existing.DeleteAsync(0); }
        var o = $.CreatePanel("Panel", r, "DWB_Overlay");
        o.style.width = "100%"; o.style.height = "100%";
        o.style.backgroundColor = C_SCRIM;
        o.style.zIndex = "9998";
        o.hittest = true; o.hittestchildren = true;
        o.SetPanelEvent("onactivate", function () {});

        var card = $.CreatePanel("Panel", o, "DWB_Card");
        card.style.width = PANEL_W + "px";
        card.style.height = PANEL_H + "px";
        card.style.horizontalAlign = "center";
        card.style.verticalAlign = "middle";
        card.style.flowChildren = "down";
        card.style.backgroundColor = C_PANEL;
        card.style.border = "1px solid " + C_LINE;
        card.style.padding = "22px 26px 20px 26px";
        card.style.boxShadow = "0px 6px 40px 6px rgba(0,0,0,0.65)";

        var titleRow = $.CreatePanel("Panel", card, "");
        titleRow.style.flowChildren = "right"; titleRow.style.width = "100%"; titleRow.style.marginBottom = "2px";
        var title = $.CreatePanel("Label", titleRow, "");
        title.text = "DEADWORKS SERVERS";
        title.style.fontSize = "30px";
        title.style.fontFamily = F_TITLE;
        title.style.color = C_TEXT;
        title.style.letterSpacing = "2px";
        title.style.width = "fill-parent-flow(1.0)";
        title.style.verticalAlign = "middle";
        var refreshBtn = mkButton(titleRow, "REFRESH", function () { if (!_busy) startFetch(); });
        var closeBtn = mkButton(titleRow, "CLOSE", closeOverlay);
        closeBtn.style.marginLeft = "8px";

        var rule = $.CreatePanel("Panel", card, "");
        rule.style.width = "100%"; rule.style.height = "2px"; rule.style.backgroundColor = C_BRAND + "66"; rule.style.marginBottom = "8px";

        _statusLabel = $.CreatePanel("Label", card, "DWB_Status");
        _statusLabel.style.fontSize = "16px";
        _statusLabel.style.fontFamily = F_BODY;
        _statusLabel.style.color = C_DIM;
        _statusLabel.style.marginBottom = "6px";
        _statusLabel.style.width = "100%";

        _progressTrack = $.CreatePanel("Panel", card, "DWB_Progress");
        _progressTrack.style.width = "100%";
        _progressTrack.style.height = "3px";
        _progressTrack.style.marginBottom = "8px";
        _progressTrack.style.backgroundColor = "rgba(255,239,215,0.10)";
        _progressTrack.style.visibility = "collapse";
        _progressFill = $.CreatePanel("Panel", _progressTrack, "DWB_ProgressFill");
        _progressFill.style.width = "0%";
        _progressFill.style.height = "100%";
        _progressFill.style.backgroundColor = C_BRAND;
        _progressFill.style.boxShadow = "0px 0px 6px 0px " + C_BRAND + "88";
        _progressFill.style.transitionProperty = "width";
        _progressFill.style.transitionDuration = "0.18s";
        _progressFill.style.transitionTimingFunction = "ease-out";

        var head = $.CreatePanel("Panel", card, "");
        head.style.flowChildren = "right";
        head.style.width = "100%";
        head.style.marginBottom = "4px";
        head.style.padding = "0px 13px";
        mkHeadCell(head, "", COL_FLAG);
        mkHeadCell(head, "SERVER", 0);
        mkHeadCell(head, "MAP", COL_MAP);
        mkHeadCell(head, "PLAYERS", COL_PLAYERS);
        mkHeadCell(head, "PING", COL_PING);
        mkHeadCell(head, "", COL_JOIN);

        var scroller = $.CreatePanel("Panel", card, "DWB_Scroller");
        scroller.style.width = "100%";
        scroller.style.height = "fill-parent-flow(1.0)";
        scroller.style.overflow = "squish scroll";
        scroller.style.backgroundColor = C_WELL;
        scroller.style.border = "1px solid " + C_LINE;
        _listPanel = $.CreatePanel("Panel", scroller, "DWB_List");
        _listPanel.style.flowChildren = "down"; _listPanel.style.width = "100%";

        var ad = $.CreatePanel("Panel", card, "DWB_Ad");
        ad.style.flowChildren = "right";
        ad.style.width = "100%";
        ad.style.marginTop = "10px";
        ad.style.padding = "8px 12px";
        ad.style.backgroundColor = C_BLURPLE + "14";
        ad.style.border = "1px solid " + C_BLURPLE + "59";

        var adIcon = $.CreatePanel("Image", ad, "DWB_AdIcon");
        adIcon.style.width = "22px";
        adIcon.style.height = "22px";
        adIcon.style.verticalAlign = "middle";
        adIcon.style.marginRight = "10px";
        adIcon.SetImage("s2r://panorama/images/discord.vtex");

        var adRow = $.CreatePanel("Panel", ad, "");
        adRow.style.flowChildren = "right"; adRow.style.verticalAlign = "middle";
        var adPre = $.CreatePanel("Label", adRow, "DWB_AdPre");
        var adLink = $.CreatePanel("Label", adRow, "DWB_AdLink");
        var adPost = $.CreatePanel("Label", adRow, "DWB_AdPost");
        adPre.text = "We need more people to make and run Deadlock servers! Join ";
        adLink.text = "deadworks.net/discord";
        adPost.text = " to find out how";
        adLink.style.color = C_BLURPLE;
        var parts = [adPre, adLink, adPost];
        for (var ai = 0; ai < parts.length; ai++) {
            parts[ai].style.fontSize = "15px";
            parts[ai].style.fontFamily = F_BODY;
            parts[ai].style.verticalAlign = "middle";
            if (ai !== 1) parts[ai].style.color = C_DIM;
        }

        _fetchHost = makeHost(o);

        _overlay = o;
        return o;
    }

    function mkButton(parent, text, onClick, primary) {
        var b = $.CreatePanel("Button", parent, "");
        b.style.padding = "7px 16px";
        b.style.backgroundColor = primary ? C_BRAND : "rgba(255,239,215,0.06)";
        b.style.border = "1px solid " + (primary ? C_BRAND : C_LINE);
        b.style.verticalAlign = "middle";
        var lbl = $.CreatePanel("Label", b, "");
        lbl.text = text;
        lbl.style.color = primary ? "#10130D" : C_TEXT;
        lbl.style.fontSize = "15px";
        lbl.style.fontFamily = F_TITLE;
        lbl.style.letterSpacing = "1px";
        b.SetPanelEvent("onactivate", onClick);
        b.SetPanelEvent("onmouseover", function () {
            b.style.backgroundColor = primary ? "#7CF5B4" : "rgba(255,239,215,0.14)";
        });
        b.SetPanelEvent("onmouseout", function () {
            b.style.backgroundColor = primary ? C_BRAND : "rgba(255,239,215,0.06)";
        });
        return b;
    }

    var BAR_MAX_H = 18;

    function mkSignalBars(parent, level) {
        var wrap = $.CreatePanel("Panel", parent, "");
        wrap.style.width = COL_PING + "px";
        wrap.style.height = "fit-children";
        wrap.style.flowChildren = "down";
        wrap.style.verticalAlign = "middle";
        setSignalBars(wrap, level);
        return wrap;
    }

    function setSignalBars(wrap, level, msText) {
        wrap.RemoveAndDeleteChildren();

        var bars = $.CreatePanel("Panel", wrap, "");
        bars.style.width = "fit-children";
        bars.style.height = BAR_MAX_H + "px";
        bars.style.flowChildren = "right";
        bars.style.horizontalAlign = "center";

        var heights = [6, 9, 12, 15, 18];
        for (var i = 0; i < 5; i++) {
            var bar = $.CreatePanel("Panel", bars, "");
            var on = level > 0 && i < level;
            bar.style.width = "4px";
            bar.style.height = heights[i] + "px";
            if (i < 4) bar.style.marginRight = "2px";
            bar.style.verticalAlign = "bottom";
            bar.style.backgroundColor = on ? (level >= 4 ? C_BRAND : C_GOLD) : "rgba(255,239,215,0.13)";
        }

        if (!msText) return;
        var lbl = $.CreatePanel("Label", wrap, "");
        lbl.text = msText;
        lbl.style.fontSize = "11px";
        lbl.style.fontFamily = F_BODY;
        lbl.style.color = level >= 4 ? C_BRAND : (level > 0 ? C_GOLD : C_DIM);
        lbl.style.width = "100%";
        lbl.style.textAlign = "center";
        lbl.style.marginTop = "3px";
    }

    function mkFlag(parent, code) {
        var img = $.CreatePanel("Image", parent, "");
        img.style.width = "24px";
        img.style.height = "24px";
        img.style.verticalAlign = "middle";
        img.style.marginRight = "10px";
        if (code) {
            img.SetImage("s2r://panorama/images/flags/" + code + ".vtex");
        }
        return img;
    }

    function openOverlay() {
        ensureOverlay();
        _overlay.visible = true;
        var held = loadHeld();
        if (held && held.servers.length) {
            renderList(held.servers);
            setStatus(held.servers.length + " server" + (held.servers.length === 1 ? "" : "s") + " · refreshing…");
        }
        startFetch();
    }
    function closeOverlay() {
        if (_overlay) { _overlay.visible = false; }
    }

    function setStatus(s) { if (_statusLabel) _statusLabel.text = s; }

    function showProgress(frac) {
        if (!_progressTrack || !_progressFill) return;
        var pct = Math.max(0, Math.min(1, frac || 0)) * 100;
        _progressTrack.style.visibility = "visible";
        _progressFill.style.width = pct.toFixed(1) + "%";
    }

    function hideProgress() {
        if (!_progressTrack || !_progressFill) return;
        _progressTrack.style.visibility = "collapse";
        _progressFill.style.width = "0%";
    }

    function clearList() { if (_listPanel) _listPanel.RemoveAndDeleteChildren(); }

    function overlayOpen() {
        return !!(_overlay && _overlay.IsValid() && _overlay.visible);
    }

    function startFetch() {
        if (_busy) return;
        beginFetch();
    }

    var _listBase = BASE_URL;

    function pickListSource(host, done) {
        ensureLauncher(host,
            function () { done("http://127.0.0.1:" + _lport, true); },
            function () { done(BASE_URL, false); });
    }

    function beginFetch() {
        if (_busy) return;
        _busy = true;
        ensureOverlay();
        var empty = true;
        empty = !_listPanel || _listPanel.GetChildCount() === 0;
        if (empty) setStatus("Loading servers…");
        var host = fetchHost();
        showProgress(0);

        function run(base, viaLauncher) {
            _listBase = base;
            _pollStep = viaLauncher ? POLL_STEP_LOCAL : POLL_STEP;
            _parallel = viaLauncher ? PARALLEL_LOCAL : PARALLEL;
            var t0 = Date.now();
            fetchServerList(host,
                function (servers) {
                    _busy = false;
                    renderList(servers);
                },
                function (reason) {
                    if (viaLauncher) {
                        _busy = false;
                        _busy = true;
                        run(BASE_URL, false);
                        return;
                    }
                    _busy = false;
                    hideProgress();
                    setStatus("Couldn't load servers (" + reason + "). Try Refresh.");
                },
                function (done, total) { showProgress(total ? (done / total) : 0); }
            );
        }

        pickListSource(host, function (base, viaLauncher) {
            run(base, viaLauncher);
        });
    }

    function renderList(servers) {
        hideProgress();
        clearList();
        _renderGen++;
        _rowBars = [];
        if (!servers.length) { setStatus("No Deadworks servers online right now."); return; }
        setStatus(servers.length + " server" + (servers.length === 1 ? "" : "s") + " online");
        for (var i = 0; i < servers.length; i++) renderRow(servers[i], i);
        enrichPings(servers, _renderGen);
    }

    var _rowBars = [], _renderGen = 0;
    var PING_FAILED = 4095;
    var PING_ROUNDS = 6;
    var PING_ROUND_S = 0.45;
    var PING_FIRST_S = 0.8;
    var PING_PARALLEL = 4;

    function barsFromMs(ms) {
        if (ms <= 0 || ms >= PING_FAILED) return 0;
        if (ms <= 45) return 5;
        if (ms <= 80) return 4;
        if (ms <= 130) return 3;
        if (ms <= 200) return 2;
        return 1;
    }

    function enrichPings(servers, gen) {
        var host = fetchHost();
        ensureLauncher(host, function () {
            if (gen !== _renderGen) return;
            var list = [];
            for (var i = 0; i < servers.length; i++) list.push(servers[i].address);
            loadImage(host, launcherUrl(_lport, "ping", "a=" + list.join(",")), function (w, h) {
                if (gen !== _renderGen) return;
                var p = decPair(w, h, _cal);
                if (p[0] !== 1) {  return; }
                $.Schedule(PING_FIRST_S, function () {
                    pollPings(host, gen, p[1], servers.length, 0, {});
                });
            }, function (r) {
                forgetLauncher();
            }, L_TIMEOUT_MS);
        }, function (r) {
        });
    }

    function pollPings(host, gen, seq, count, round, got) {
        if (gen !== _renderGen) return;
        var queue = [];
        for (var i = 0; i < count; i++) if (got[i] === undefined) queue.push(i);
        if (!queue.length || round >= PING_ROUNDS) {
            var n = 0;
            for (var k in got) if (got.hasOwnProperty(k)) n++;
            return;
        }
        var active = 0, qi = 0, outstanding = queue.length;
        function settle() {
            active--;
            outstanding--;
            fill();
            if (outstanding <= 0) {
                $.Schedule(PING_ROUND_S, function () { pollPings(host, gen, seq, count, round + 1, got); });
            }
        }
        function one(idx) {
            active++;
            loadImage(host, launcherUrl(_lport, "pingres", "b=" + seq + "&i=" + idx), function (w, h) {
                if (gen === _renderGen) {
                    var p = decPair(w, h, _cal);
                    var ms = (p[0] | (p[1] << 6));
                    if (ms !== 0) { got[idx] = ms; applyPing(idx, ms); }
                }
                settle();
            }, settle, L_TIMEOUT_MS);
        }
        function fill() { while (active < PING_PARALLEL && qi < queue.length) one(queue[qi++]); }
        fill();
    }

    function applyPing(idx, ms) {
        var wrap = _rowBars[idx];
        if (!wrap || !wrap.IsValid()) return;
        setSignalBars(wrap, barsFromMs(ms), ms >= PING_FAILED ? "—" : String(ms));
    }

    function renderRow(server, i) {
        var row = $.CreatePanel("Panel", _listPanel, "");
        row.style.flowChildren = "right";
        row.style.width = "100%";
        row.style.padding = "9px 12px";
        row.style.backgroundColor = (i % 2) ? "rgba(255,239,215,0.035)" : "rgba(0,0,0,0)";
        row.style.borderBottom = "1px solid rgba(255,239,215,0.07)";
        row.SetPanelEvent("onmouseover", function () {
            row.style.backgroundColor = "rgba(95,230,158,0.10)";
        });
        row.SetPanelEvent("onmouseout", function () {
            row.style.backgroundColor = (i % 2) ? "rgba(255,239,215,0.035)" : "rgba(0,0,0,0)";
        });

        mkFlag(row, server.country);

        var nameCol = $.CreatePanel("Panel", row, "");
        nameCol.style.flowChildren = "down"; nameCol.style.width = "fill-parent-flow(1.0)"; nameCol.style.verticalAlign = "middle";
        var name = $.CreatePanel("Label", nameCol, "");
        name.text = server.name || server.address;
        name.style.fontSize = "19px";
        name.style.fontFamily = F_BODY;
        name.style.color = C_TEXT;
        name.style.width = "100%";
        name.style.whiteSpace = "nowrap";
        name.style.textOverflow = "ellipsis";
        var sub = $.CreatePanel("Label", nameCol, "");
        var subText = server.address;
        if (server.requiresLauncher) subText += "   ·   custom content";
        sub.text = subText;
        sub.style.fontSize = "13px";
        sub.style.fontFamily = F_BODY;
        sub.style.color = server.requiresLauncher ? C_GOLD + "cc" : C_DIM;
        sub.style.width = "100%";
        sub.style.whiteSpace = "nowrap";
        sub.style.textOverflow = "ellipsis";

        var map = $.CreatePanel("Label", row, "");
        map.text = server.map ? mapDisplay(server.map) : "Custom";
        map.style.width = COL_MAP + "px";
        map.style.fontSize = "16px";
        map.style.fontFamily = F_BODY;
        map.style.color = C_DIM;
        map.style.verticalAlign = "middle";

        var players = $.CreatePanel("Label", row, "");
        players.text = server.players + " / " + server.max;
        players.style.width = COL_PLAYERS + "px";
        players.style.fontSize = "18px";
        players.style.fontFamily = F_BODY;
        players.style.color = server.players >= server.max ? "#FF410D"
                            : server.players > 0 ? C_BRAND : C_DIM;
        players.style.verticalAlign = "middle";
        players.style.textAlign = "center";

        _rowBars[i] = mkSignalBars(row, server.ping);

        var joinWrap = $.CreatePanel("Panel", row, "");
        joinWrap.style.width = COL_JOIN + "px"; joinWrap.style.verticalAlign = "middle"; joinWrap.style.flowChildren = "right";
        var joinBtn = mkButton(joinWrap, "JOIN", function () { onJoinClicked(server); }, true);
        joinBtn.style.horizontalAlign = "right";
    }

    function mapDisplay(map) {
        var m = { dl_streets: "The Streets", dl_midtown: "Midtown", dl_hideout: "Hideout",
                  street_test: "Street Test", hero_testing: "Hero Sandbox", "1v1_test": "1v1",
                  new_player_basics: "Tutorial", start: "Start" };
        return m[map] || map;
    }

    var _prep = null;

    function fetchHost() {
        if (_fetchHost && _fetchHost.IsValid()) return _fetchHost;
        return (_fetchHost = makeHost(_overlay));
    }

    function ensurePrepModal() {
        if (_prep && _prep.modal.IsValid()) return _prep;

        var modal = $.CreatePanel("Panel", _overlay, "DWB_Prep");
        modal.style.width = "100%"; modal.style.height = "100%";
        modal.style.backgroundColor = "rgba(8,10,7,0.72)";
        modal.style.zIndex = "1100";

        var card = $.CreatePanel("Panel", modal, "");
        card.style.width = "720px";
        card.style.horizontalAlign = "center";
        card.style.verticalAlign = "middle";
        card.style.flowChildren = "down";
        card.style.backgroundColor = C_PANEL;
        card.style.border = "1px solid " + C_LINE;
        card.style.padding = "22px 26px 18px 26px";
        card.style.boxShadow = "0px 6px 40px 6px rgba(0,0,0,0.65)";

        var titleLbl = $.CreatePanel("Label", card, "DWB_PrepTitle");
        titleLbl.text = TITLE_PROGRESS;
        titleLbl.style.fontSize = "22px";
        titleLbl.style.fontFamily = F_TITLE;
        titleLbl.style.color = C_TEXT;
        titleLbl.style.letterSpacing = "2px";

        var rule = $.CreatePanel("Panel", card, "");
        rule.style.width = "100%"; rule.style.height = "2px";
        rule.style.backgroundColor = C_BRAND + "66";
        rule.style.marginTop = "4px"; rule.style.marginBottom = "12px";

        var progressBox = $.CreatePanel("Panel", card, "DWB_PrepProgressBox");
        progressBox.style.flowChildren = "down"; progressBox.style.width = "100%";

        var nameLbl = $.CreatePanel("Label", progressBox, "DWB_PrepName");
        nameLbl.style.fontSize = "19px"; nameLbl.style.fontFamily = F_BODY;
        nameLbl.style.color = C_TEXT; nameLbl.style.width = "100%";

        var phaseLbl = $.CreatePanel("Label", progressBox, "DWB_PrepPhase");
        phaseLbl.style.fontSize = "16px"; phaseLbl.style.fontFamily = F_BODY;
        phaseLbl.style.color = C_DIM; phaseLbl.style.width = "100%";
        phaseLbl.style.marginTop = "2px"; phaseLbl.style.marginBottom = "10px";

        var track = $.CreatePanel("Panel", progressBox, "DWB_PrepBar");
        track.style.width = "100%"; track.style.height = "6px";
        track.style.backgroundColor = "rgba(255,239,215,0.10)";
        var fill = $.CreatePanel("Panel", track, "DWB_PrepFill");
        fill.style.width = "0%"; fill.style.height = "100%";
        fill.style.backgroundColor = C_BRAND;
        fill.style.boxShadow = "0px 0px 6px 0px " + C_BRAND + "88";
        fill.style.transitionProperty = "width";
        fill.style.transitionDuration = "0.25s";
        fill.style.transitionTimingFunction = "ease-out";

        var detailLbl = $.CreatePanel("Label", progressBox, "DWB_PrepDetail");
        detailLbl.style.fontSize = "13px"; detailLbl.style.fontFamily = F_BODY;
        detailLbl.style.color = C_DIM; detailLbl.style.width = "100%";
        detailLbl.style.marginTop = "8px";

        var missingBox = $.CreatePanel("Panel", card, "DWB_PrepMissingBox");
        missingBox.style.flowChildren = "down";
        missingBox.style.width = "100%";
        missingBox.style.visibility = "collapse";

        var missLine1 = $.CreatePanel("Label", missingBox, "DWB_MissLine1");
        missLine1.style.fontSize = "16px"; missLine1.style.fontFamily = F_BODY;
        missLine1.style.color = C_TEXT; missLine1.style.width = "100%";

        var linkRow = $.CreatePanel("Panel", missingBox, "DWB_MissLinkRow");
        linkRow.style.flowChildren = "right"; linkRow.style.width = "100%";
        linkRow.style.marginTop = "6px";
        var missLine2 = $.CreatePanel("Label", linkRow, "DWB_MissLine2");
        missLine2.text = "If you do not have the launcher, consider downloading from ";
        missLine2.style.fontSize = "16px"; missLine2.style.fontFamily = F_BODY;
        missLine2.style.color = C_TEXT;
        var missLink = $.CreatePanel("Label", linkRow, "DWB_MissLink");
        missLink.text = "deadworks.net";
        missLink.style.fontSize = "16px"; missLink.style.fontFamily = F_BODY;
        missLink.style.color = C_BRAND;

        var missWarn = $.CreatePanel("Label", missingBox, "DWB_MissWarn");
        missWarn.text = "You may not be able to see custom content without the launcher.";
        missWarn.style.fontSize = "16px"; missWarn.style.fontFamily = F_BODY;
        missWarn.style.fontWeight = "bold";
        missWarn.style.color = C_TEXT; missWarn.style.width = "100%";
        missWarn.style.marginTop = "14px";

        var btnRow = $.CreatePanel("Panel", card, "DWB_PrepButtons");
        btnRow.style.flowChildren = "right"; btnRow.style.width = "100%";
        btnRow.style.horizontalAlign = "right"; btnRow.style.marginTop = "16px";

        _prep = { modal: modal, titleLbl: titleLbl, progressBox: progressBox,
                  nameLbl: nameLbl, phaseLbl: phaseLbl, track: track, fill: fill,
                  detailLbl: detailLbl, missingBox: missingBox,
                  missLine1: missLine1, btnRow: btnRow };
        return _prep;
    }

    function prepPhase(t) { _prep.phaseLbl.text = t; }
    function prepDetail(t) { _prep.detailLbl.text = t; }
    function prepBar(frac) {
        var pct = Math.max(0, Math.min(1, frac || 0)) * 100;
        _prep.fill.style.width = pct.toFixed(1) + "%";
    }
    function prepButtons(defs) {
        _prep.btnRow.RemoveAndDeleteChildren();
        for (var i = 0; i < defs.length; i++) {
            var b = mkButton(_prep.btnRow, defs[i].text, defs[i].fn, !!defs[i].primary);
            if (i > 0) b.style.marginLeft = "8px";
        }
    }

    function closePrepare() {
        if (!_prep) return;
        _prep.active = false;
        _prep.modal.visible = false;
    }

    function sizeText(mib) {
        if (!mib) return "";
        return mib >= 1024 ? ((mib / 1024).toFixed(1) + " GB") : (mib + " MB");
    }

    var L_ERRORS = {
        1: "That server isn't online right now.",
        2: "Couldn't reach the Deadworks API to look up its content.",
        3: "Couldn't write to your Deadlock folder.",
        4: "A downloaded file was corrupt.",
        5: "The launcher hit an unexpected error.",
        6: "Deadlock still has the old file open. Disconnect from your current server first.",
        7: "The launcher needs to patch gameinfo.gi. Restart it with Deadlock closed."
    };

    function joinAnyway(server) {
        closePrepare();
        closeOverlay();
        setStatus("Connecting to " + (server.name || server.address) + "…");
        connectTo(server);
    }

    function prepFail(server, msg) {
        prepPhase(msg);
        prepBar(0);
        prepDetail("");
        _prep.active = false;
        prepButtons([
            { text: "RETRY", fn: function () { openPrepare(server); }, primary: true },
            { text: "JOIN ANYWAY", fn: function () { joinAnyway(server); } },
            { text: "CLOSE", fn: closePrepare }
        ]);
    }

    function openPrepare(server, verifyOnly) {
        ensureOverlay();
        ensurePrepModal();
        _prep.verifyOnly = !!verifyOnly;
        _prep.modal.visible = true;
        _prep.server = server;
        _prep.seq = null;
        _prep.tick = 0;
        _prep.misses = 0;
        _prep.idle = 0;
        _prep.mib = null;
        _prep.filesDone = 0;
        _prep.filesTotal = 0;
        _prep.state = LS_QUEUED;
        _prep.pct = 0;
        _prep.active = true;
        _prep.launcherOk = false;
        _prep.unavailableShown = false;
        _prep.titleLbl.text = verifyOnly ? TITLE_CONNECTING : TITLE_PROGRESS;
        _prep.progressBox.style.visibility = "visible";
        _prep.missingBox.style.visibility = "collapse";
        _prep.track.style.visibility = verifyOnly ? "collapse" : "visible";
        var gen = _prep.gen = (_prep.gen || 0) + 1;
        _prep.nameLbl.text = server.name || server.address;
        prepBar(0);
        prepDetail("");
        prepPhase(verifyOnly ? "Checking for the Deadworks launcher…"
                             : "Contacting the Deadworks launcher…");
        prepButtons([{ text: "CANCEL", fn: function () { cancelPrepare(); } }]);

        var host = fetchHost();

        $.Schedule(L_WATCHDOG_S, function () {
            if (!live(gen) || _prep.launcherOk) return;
            launcherUnavailable(server, gen, "timeout");
        });

        ensureLauncher(host, function () {
            if (!live(gen)) return;
            _prep.launcherOk = true;
            if (_prep.verifyOnly) {
                _prep.active = false;
                closePrepare();
                joinAnyway(server);
                return;
            }
            prepPhase("Asking the launcher for this server's content…");
            launcherPrep(host, server.address, function (seq) {
                if (!live(gen)) return;
                _prep.seq = seq;
                tickJob(gen);
            }, function (r) {
                if (!live(gen)) return;
                if (r === "refused") prepFail(server, "The launcher refused the request.");
                else launcherUnavailable(server, gen, r);
            });
        }, function (r) {
            if (!live(gen)) return;
            launcherUnavailable(server, gen, r);
        });
    }

    function launcherUnavailable(server, gen, reason) {
        if (!live(gen) || _prep.unavailableShown) return;
        _prep.unavailableShown = true;
        _prep.active = false;

        if (skipLauncherPrompt()) {
            joinAnyway(server);
            return;
        }

        var outOfDate = (reason === "proto");
        _prep.modal.visible = true;
        _prep.titleLbl.text = outOfDate ? TITLE_OUTDATED : TITLE_MISSING;
        _prep.progressBox.style.visibility = "collapse";
        _prep.missingBox.style.visibility = "visible";
        _prep.missLine1.text = outOfDate
            ? "Your Deadworks launcher is too old to talk to this build. Update it, or close and reopen it."
            : "The Deadworks launcher is not running. If you have it installed, make sure it is open.";

        prepButtons([
            { text: "Connect anyway", fn: function () { joinAnyway(server); }, primary: true },
            { text: "Connect anyway and don't ask me again this session",
              fn: function () { setSkipLauncherPrompt(); joinAnyway(server); } },
            { text: "Cancel", fn: closePrepare }
        ]);
    }

    function cancelPrepare() {
        if (_prep && _prep.seq !== null) launcherCancel(fetchHost(), _prep.seq);
        closePrepare();
    }

    function phaseText(state) {
        switch (state) {
            case LS_QUEUED: return "Queued…";
            case LS_RESOLVING: return "Looking up this server's content…";
            case LS_DOWNLOADING: return "Downloading custom content…";
            case LS_INSTALLING: return "Installing…";
            case LS_READY: return "Ready — connecting…";
            default: return "Working…";
        }
    }

    function renderPrep() {
        prepPhase(phaseText(_prep.state));
        prepBar(_prep.pct / 63);
        var bits = [];
        if (_prep.filesTotal) bits.push(_prep.filesDone + " of " + _prep.filesTotal + " files");
        var sz = sizeText(_prep.mib);
        if (sz) bits.push(sz);
        prepDetail(bits.join("   ·   "));
    }

    function live(gen) { return !!(_prep && _prep.active && _prep.gen === gen); }

    function tickJob(gen) {
        if (!live(gen)) return;
        var host = fetchHost();
        var server = _prep.server;

        launcherStatus(host, _prep.seq, 0, function (p) {
            if (!live(gen)) return;
            _prep.misses = 0;
            _prep.state = p[0];
            _prep.pct = p[1];

            if (p[0] === LS_ERROR) {
                launcherStatus(host, _prep.seq, 1, function (q) {
                    if (!live(gen)) return;
                    prepFail(server, L_ERRORS[q[0]] || "The download failed.");
                }, function () {
                    if (!live(gen)) return;
                    prepFail(server, "The download failed.");
                });
                return;
            }
            if (p[0] === LS_CANCELLED) { closePrepare(); return; }
            if (p[0] === LS_IDLE) {
                if (++_prep.idle >= 3) { prepFail(server, "The launcher lost track of that download."); return; }
            } else {
                _prep.idle = 0;
            }

            renderPrep();

            if (p[0] === LS_READY) {
                _prep.active = false;
                prepBar(1);
                prepPhase("Ready — connecting…");
                prepButtons([]);
                $.Schedule(0.4, function () {
                    closePrepare();
                    closeOverlay();
                    connectTo(server);
                });
                return;
            }
            pollDetails(host, gen);
        }, function () {
            if (!live(gen)) return;
            if (++_prep.misses >= L_MAX_MISSES) {
                forgetLauncher();
                prepFail(server, "Lost contact with the Deadworks launcher.");
                return;
            }
            $.Schedule(L_POLL_S, function () { tickJob(gen); });
        });
    }

    function pollDetails(host, gen) {
        var next = function () {
            if (!live(gen)) return;
            _prep.tick++;
            $.Schedule(L_POLL_S, function () { tickJob(gen); });
        };
        if (_prep.tick % L_DETAIL_EVERY !== 0) { next(); return; }
        launcherStatus(host, _prep.seq, 1, function (q) {
            if (!live(gen)) return;
            _prep.filesDone = q[0];
            _prep.filesTotal = q[1];
            renderPrep();
            if (_prep.mib) { next(); return; }
            launcherStatus(host, _prep.seq, 2, function (r) {
                if (!live(gen)) return;
                _prep.mib = r[0] | (r[1] << 6);
                renderPrep();
                next();
            }, next);
        }, next);
    }

    function onJoinClicked(server) {
        if (!server.requiresLauncher && skipLauncherPrompt()) {
            joinAnyway(server);
            return;
        }
        openPrepare(server, !server.requiresLauncher);
    }

    var TILE_ID = "Option_DeadworksServers";

    function bindTile(attempt) {
        attempt = attempt || 0;
        var btn = null;
        btn = root().FindChildTraverse(TILE_ID);
        if (btn) {
            btn.SetPanelEvent("onactivate", openOverlay);
            styleTile();
            return;
        }
        var container = null;
        container = root().FindChildTraverse("SubModesContainerTop");
        if (container && !isInsideLegacyPlayMenu(container)) { injectFallbackTile(container); return; }
        if (attempt < 12) $.Schedule(0.5, function () { bindTile(attempt + 1); });
    }

    var CARD_BG = "url('s2r://panorama/images/main_menu/play/card_custom_psd.vtex')";

    function styleTile() {
        var bg = null;
        bg = root().FindChildTraverse("DWB_CardBg");
        if (!bg) {  return; }
        bg.style.backgroundImage = CARD_BG;
    }

    function isInsideLegacyPlayMenu(panel) {
        var p = panel, depth = 0;
        while (p && depth < 12) {
            if (p.id === "PlayMenu") return true;
            p = p.GetParent();
            depth++;
        }
        return false;
    }

    function injectFallbackTile(container) {
        var existing = null;
        existing = container.FindChildTraverse(TILE_ID);
        if (existing) { existing.SetPanelEvent("onactivate", openOverlay); return; }
        var btn = $.CreatePanel("Button", container, TILE_ID);
        btn.AddClass("playoption"); btn.AddClass("smallButton");
        btn.SetPanelEvent("onactivate", openOverlay);
        var bg = $.CreatePanel("Panel", btn, "DWB_CardBg");
        bg.AddClass("backgroundImg");
        bg.style.backgroundImage = CARD_BG;
        $.CreatePanel("Panel", btn, "DWB_CardArt").AddClass("playImage");
        var textArea = $.CreatePanel("Panel", btn, "");
        textArea.AddClass("TextArea");
        var titleArea = $.CreatePanel("Panel", textArea, "");
        titleArea.AddClass("TitleArea");
        var titleLbl = $.CreatePanel("Label", titleArea, "");
        titleLbl.text = "Deadworks Servers"; titleLbl.AddClass("playoptionLabel");
        var descArea = $.CreatePanel("Panel", textArea, "");
        descArea.AddClass("DescArea");
        var descLbl = $.CreatePanel("Label", descArea, "");
        descLbl.text = "Browse and join community servers"; descLbl.AddClass("playModeDesc");
    }

    globalThis.DWBrowser = {
        open: openOverlay,
        close: closeOverlay,
        refresh: startFetch,
        setParallel: function (n) { PARALLEL = Math.max(1, n | 0); return PARALLEL; },
        setBaseUrl: function (u) { BASE_URL = String(u); },
        launcher: {
            find: function () {
                _lport = null; _lverified = false;
                ensureLauncher(fetchHost(),
                    function () {  },
                    function (r) {  });
            },
            port: function () { return _lport; },
            setPortBase: function (n) {
                LPORT_BASE = n | 0;
                _lport = null; _lverified = false;
                windowRoot().SetAttributeString(ATTR_LPORT, "");
                return LPORT_BASE;
            },
            prepare: function (address, requiresLauncher) {
                onJoinClicked({ address: address, name: address,
                                requiresLauncher: requiresLauncher !== false });
            }
        },
        _internals: {
            parsePayload: parsePayload,
            lzssDecompress: lzssDecompress,
            parseRow: parseRow,
            columnarToRow: columnarToRow,
            saveHeld: saveHeld,
            loadHeld: loadHeld,
            fnvVersion: fnvVersion
        }
    };
    globalThis.DWBrowserOpen = openOverlay;

    (function () {
        var id = "?";
        id = root().id || "(unnamed)";
    })();
    $.Schedule(0.2, function () { bindTile(0); });
})();
