"use strict";

(function () {
    var DW_TAG = "[DW_BOOTSTRAP]";
    var USER_PREFIX = "u~";
    var SEP = "\x1f";
    var _hudRoot = null;
    var _subtitles = null;
    var POLL_SEC = 0.25;

    $.Msg(DW_TAG, " loaded");

    var CC_ASSERT_TICKS_WAITING = 20;
    var CC_ASSERT_TICKS_RUNNING = 120;

    var CC_LINGER = "0.1";

    function EnsureCaptionsEnabled() {
        try { $.DispatchEvent("CitadelConCommand", "closecaption 1"); }
        catch (e) { $.Msg(DW_TAG, " could not enable closecaption: ", String(e)); }
        try { $.DispatchEvent("CitadelConCommand", "cc_linger_time " + CC_LINGER); }
        catch (e) { $.Msg(DW_TAG, " could not set cc_linger_time: ", String(e)); }
    }

    function SetCaptionLinger(v) {
        if (!v || v === CC_LINGER) return;
        var n = parseFloat(v);
        if (!isFinite(n) || n < 0 || n > 5) return;
        CC_LINGER = v;
        $.Msg(DW_TAG, " server set cc_linger_time ", v);
        try { $.DispatchEvent("CitadelConCommand", "cc_linger_time " + v); }
        catch (e) {}
    }

    EnsureCaptionsEnabled();

    var _userHandlers = [];
    var _panels = {};
    var _cachedTrees = {};

    var _registrations = {};

    var _sessionToken = null;
    var _lastDwActivityMs = 0;
    var HEARTBEAT_TIMEOUT_MS = 5000;

    var STALL_GAP_MS = 1000;
    var _lastWatchdogMs = 0;

    function lookup(entry, id) {
        if (!entry.host) return null;
        var cache = entry.findCache || (entry.findCache = {});
        var hit = cache[id];
        if (hit !== undefined) {
            var alive = false;
            try { alive = !hit.IsValid || hit.IsValid(); } catch (e) { alive = false; }
            if (alive) return hit;
            delete cache[id];
        }
        var el = null;
        try { el = entry.host.FindChildTraverse(id); } catch (e) {}
        if (el) cache[id] = el;
        return el;
    }

    function makeWrapper(entry) {
        function on(id, eventName, fn) {
            var el = lookup(entry, id);
            if (el && el.SetPanelEvent) {
                try { el.SetPanelEvent(String(eventName), fn); return true; }
                catch (e) { $.Msg(DW_TAG, " on('", id, "','", eventName, "') failed: ", String(e)); }
            }
            return false;
        }
        return {
            host: entry.host,
            find: function (id) { return lookup(entry, id); },
            on: on,
            onClick: function (id, fn) { return on(id, "onactivate", fn); },
            text: function (id, value) {
                var el = lookup(entry, id);
                if (!el) return false;
                try { el.text = (value == null) ? "" : String(value); return true; }
                catch (e) { return false; }
            },
            connected: function () { return _sessionToken !== null; },
            get: function (key, fallback) {
                var v = entry.state[String(key)];
                if (v !== undefined) return v;
                return (fallback === undefined) ? "" : fallback;
            },
            set: function (key, val) {
                var k = String(key);
                entry.state[k] = (val == null) ? "" : String(val);
                applyAutoBind(entry, [k]);
                runRender(entry, [k]);
            },
            clear: function () {
                entry.state = {};
                runRender(entry, null);
            },
            send: function (eventName) {
                var parts = [entry.id, String(eventName)];
                for (var i = 1; i < arguments.length; i++) parts.push(String(arguments[i]));
                var cmd = "dw_ui " + parts.join("|");
                try { $.DispatchEvent("CitadelConCommand", cmd); }
                catch (e) { $.Msg(DW_TAG, " send failed: ", String(e)); }
            }
        };
    }

    function runRender(entry, changed) {
        if (!entry.ready) return;
        var fn = entry.cfg && entry.cfg.render;
        if (typeof fn !== "function") return;
        try { fn(entry.wrapper, entry.state, changed || null); }
        catch (e) { $.Msg(DW_TAG, " render '", entry.id, "' threw: ", String(e)); }
    }

    function applyAutoBind(entry, keys) {
        if (!entry.host) return 0;
        var n = 0;
        if (keys) {
            for (var i = 0; i < keys.length; i++) {
                var el = lookup(entry, keys[i]);
                if (el && el.paneltype === "Label") { el.text = entry.state[keys[i]]; n++; }
            }
        } else {
            for (var k in entry.state) {
                if (!Object.prototype.hasOwnProperty.call(entry.state, k)) continue;
                var e2 = lookup(entry, k);
                if (e2 && e2.paneltype === "Label") { e2.text = entry.state[k]; n++; }
            }
        }
        return n;
    }

    var ATTR_VAL = "dwv_";
    var ATTR_SYS = "dw_";

    var PROTO = 1;
    var WIRE_SEP = String.fromCharCode(31);

    function pushAttributes(entry, changed) {
        var host = entry.host;
        if (!host || !host.SetAttributeString) return;
        try {
            var keys = [];
            for (var k in entry.state) {
                if (Object.prototype.hasOwnProperty.call(entry.state, k)) keys.push(k);
            }
            var write = changed || keys;
            for (var i = 0; i < write.length; i++) {
                host.SetAttributeString(ATTR_VAL + write[i], entry.state[write[i]] || "");
            }
            entry.attrSeq = (entry.attrSeq || 0) + 1;
            host.SetAttributeString(ATTR_SYS + "proto", String(PROTO));
            host.SetAttributeString(ATTR_SYS + "id", entry.id);
            host.SetAttributeString(ATTR_SYS + "keys", keys.join(","));
            host.SetAttributeString(ATTR_SYS + "changed", changed ? changed.join(",") : "");
            host.SetAttributeString(ATTR_SYS + "live", _sessionToken !== null ? "1" : "0");
            host.SetAttributeString(ATTR_SYS + "seq", String(entry.attrSeq));
            if (entry.attrSeq === 1) {
                $.Msg(DW_TAG, " attribute channel open for '", entry.id, "' (", keys.length, " key(s))");
            }

            var wire = lookup(entry, "DWWire");
            if (wire) {
                var parts = [String(entry.attrSeq),
                             _sessionToken !== null ? "1" : "0",
                             entry.id,
                             changed ? changed.join(",") : ""];
                for (var w = 0; w < keys.length; w++) {
                    parts.push(keys[w]);
                    parts.push(entry.state[keys[w]] || "");
                }
                try { wire.text = parts.join(WIRE_SEP); } catch (e2) {}
            }
        } catch (e) {
            WarnThrottled("attr:" + entry.id,
                "attribute push failed for '" + entry.id + "': " + String(e));
            return;
        }
        wakeAddon(entry);
    }

    function pushRaw(entry, text) {
        var host = entry.host;
        if (!host || !host.SetAttributeString) return;
        entry.rawSeq = (entry.rawSeq || 0) + 1;
        try {
            host.SetAttributeString(ATTR_SYS + "raw", text);
            host.SetAttributeString(ATTR_SYS + "rawseq", String(entry.rawSeq));
        } catch (e) {
            WarnThrottled("raw:" + entry.id, "raw push failed for '" + entry.id + "'");
            return;
        }
        wakeAddon(entry);
    }

    function reportAddon(panelId, status) {
        try { $.DispatchEvent("CitadelConCommand", "dw_ui ~|addon|" + panelId + "|" + status); }
        catch (e) { $.Msg(DW_TAG, " addon report failed for '", panelId, "': ", String(e)); }
    }

    var WAKE_FORMS = [
        function (h) { $.DispatchEvent("DispatchPanelEvent", h, "onactivate"); },
        function (h) { $.DispatchEvent("Activated", h, "mouse"); },
        function (h) { $.DispatchEvent("Activated", h, 0); }
    ];

    function wakeAddon(entry) {
        if (entry.wakeForm === -1) return;

        if (entry.wakeForm === undefined) {
            var err = "";
            for (var i = 0; i < WAKE_FORMS.length; i++) {
                try {
                    WAKE_FORMS[i](entry.host);
                    entry.wakeForm = i;
                    $.Msg(DW_TAG, " wake form ", i, " accepted for '", entry.id, "'");
                    return;
                } catch (e) { err = String(e); }
            }
            entry.wakeForm = -1;
            $.Msg(DW_TAG, " no usable wake dispatch for '", entry.id, "' (", err,
                  ") — addon polls instead, which costs 50ms and nothing else");
            return;
        }

        try { WAKE_FORMS[entry.wakeForm](entry.host); }
        catch (e) { entry.wakeForm = -1; }
    }

    function clearAutoBind(entry) {
        if (!entry.host) return;
        for (var k in entry.state) {
            if (!Object.prototype.hasOwnProperty.call(entry.state, k)) continue;
            var el = lookup(entry, k);
            if (el && el.paneltype === "Label") el.text = "";
        }
    }

    function runInit(entry) {
        var initFn = entry.cfg && entry.cfg.init;
        if (typeof initFn !== "function") return;
        try { initFn(entry.wrapper); }
        catch (e) { $.Msg(DW_TAG, " init '", entry.id, "' threw: ", String(e)); }
    }

    function teardownHost(entry) {
        if (!entry) return;
        if (entry.ready && entry.cfg && typeof entry.cfg.onDestroy === "function") {
            try { entry.cfg.onDestroy(entry.wrapper); }
            catch (e) { $.Msg(DW_TAG, " onDestroy '", entry.id, "' threw: ", String(e)); }
        }
        if (entry.host) { try { entry.host.DeleteAsync(0); } catch (e) {} }
        entry.host = null;
        entry.wrapper = null;
        entry.findCache = null;
        entry.ready = false;
        entry.xmlHost = false;
    }

    function finalizePanel(entry, host) {
        entry.host = host;
        entry.findCache = null;
        entry.wrapper = makeWrapper(entry);
        entry.ready = true;
        runInit(entry);
        applyAutoBind(entry, null);
        runRender(entry, null);
    }

    var LAYOUT_RETRY_LIMIT = 5;

    function tryLoadPanelLayout(entry, attempt) {
        attempt = attempt || 0;
        if (_panels[entry.id] !== entry) return;

        var hud = FindHudRoot();
        if (!hud) { $.Schedule(0.5, function () { tryLoadPanelLayout(entry, attempt); }); return; }
        var host = $.CreatePanel("Panel", hud, "DWHost_" + entry.id);
        var ok = false;
        try { ok = host.BLoadLayout(entry.cfg.layout, false, false); }
        catch (e) { $.Msg(DW_TAG, " BLoadLayout threw for '", entry.id, "': ", String(e)); }
        if (!ok) {
            try { host.DeleteAsync(0); } catch (e) {}
            if (attempt + 1 < LAYOUT_RETRY_LIMIT) {
                $.Schedule(1.0, function () { tryLoadPanelLayout(entry, attempt + 1); });
                return;
            }
            $.Msg(DW_TAG, " GIVING UP on panel '", entry.id, "': BLoadLayout failed ",
                  LAYOUT_RETRY_LIMIT, "x for '", entry.cfg.layout,
                  "' — check the path and that the .xml is compiled into the mod tree");
            return;
        }
        if (attempt > 0) $.Msg(DW_TAG, " panel '", entry.id, "' loaded on attempt ", attempt + 1);
        finalizePanel(entry, host);
    }

    function armRegistration(cfg, existing) {
        if (existing) teardownHost(existing);
        var entry = existing || {
            id: cfg.id, cfg: cfg, host: null, state: {},
            ready: false, wrapper: null, findCache: null
        };
        entry.cfg = cfg;
        _panels[cfg.id] = entry;
        if (cfg.layout) {
            $.Schedule(0.1, function () { tryLoadPanelLayout(entry, 0); });
        } else {
            finalizePanel(entry, null);
        }
        return entry;
    }

    function ReviveRegistered() {
        var n = 0;
        for (var id in _registrations) {
            if (!Object.prototype.hasOwnProperty.call(_registrations, id)) continue;
            armRegistration(_registrations[id], null);
            n++;
        }
        if (n > 0) $.Msg(DW_TAG, " re-armed ", n, " registered panel(s)");
    }

    globalThis.DW = {
        onUser: function (handler) {
            if (typeof handler === "function") _userHandlers.push(handler);
        },
        registerPanel: function (cfg) {
            if (!cfg || typeof cfg.id !== "string") {
                $.Msg(DW_TAG, " registerPanel requires { id }");
                return;
            }
            _registrations[cfg.id] = cfg;

            var existing = _panels[cfg.id];
            if (existing && existing.host) {
                existing.cfg = cfg;
                applyAutoBind(existing, null);
                runRender(existing, null);
                return;
            }
            armRegistration(cfg, existing);
            $.Msg(DW_TAG, " registered panel '", cfg.id, "'");
        },
        registeredPanels: function () {
            var out = [];
            for (var id in _registrations) {
                if (Object.prototype.hasOwnProperty.call(_registrations, id)) out.push(id);
            }
            return out;
        },
        connected: function () { return _sessionToken !== null; },
        serverVersion: function () { return _serverVersion; },
        getHudRoot: function () { return FindHudRoot(); },
        send: function (panelId, eventName) {
            var parts = [String(panelId), String(eventName)];
            for (var i = 2; i < arguments.length; i++) parts.push(String(arguments[i]));
            var cmd = "dw_ui " + parts.join("|");
            try { $.DispatchEvent("CitadelConCommand", cmd); }
            catch (e) { $.Msg(DW_TAG, " DW.send failed: ", String(e)); }
        },
        log: function () {
            var args = ["[DW_API]"];
            for (var i = 0; i < arguments.length; i++) args.push(arguments[i]);
            $.Msg.apply($, args);
        }
    };

    function FindHudRoot() {
        if (_hudRoot) return _hudRoot;
        var p = $.GetContextPanel();
        var best = p;
        var depth = 0;
        while (p && depth < 30) {
            best = p;
            p = p.GetParent();
            depth++;
        }
        _hudRoot = best;
        return best;
    }

    function FindByIdRecursive(node, targetId, depth) {
        if (!node || depth > 25) return null;
        try {
            if (node.id === targetId) return node;
            var n = node.GetChildCount();
            for (var i = 0; i < n; i++) {
                var c = node.GetChild(i);
                var r = FindByIdRecursive(c, targetId, depth + 1);
                if (r) return r;
            }
        } catch (e) {}
        return null;
    }

    function FindSubtitles() {
        if (_subtitles && _subtitles.IsValid && _subtitles.IsValid()) return _subtitles;
        var root = FindHudRoot();
        if (!root) return null;
        var s = null;
        try { s = root.FindChildTraverse("HudSubtitles"); } catch (e) {}
        if (!s) { try { s = root.FindChildTraverse("CitadelHudSubtitles"); } catch (e) {} }
        if (!s) s = FindByIdRecursive(root, "HudSubtitles", 0);
        if (!s) s = FindByIdRecursive(root, "CitadelHudSubtitles", 0);
        if (s) {
            _subtitles = s;
            HideSubtitles(s);
            $.Msg(DW_TAG, " found subtitles container, paneltype=", (s.paneltype || "?"), " id=", (s.id || "?"));
        }
        return _subtitles;
    }

    function HideSubtitles(s) {
        if (!s) return;
        try { s.style.opacity = "0"; } catch (e) {}
        try { s.style.visibility = "visible"; } catch (e) {}
        try { s.style.pointerEvents = "none"; } catch (e) {}
        try { s.hittest = false; } catch (e) {}
        try { s.hittestchildren = false; } catch (e) {}
    }

    function GetSubtitleText(sub) {
        try {
            var labels = sub.FindChildrenWithClassTraverse
                ? sub.FindChildrenWithClassTraverse("ForWorld")
                : null;
            if (labels && labels.length > 0) {
                var t = labels[0].text;
                if (typeof t === "string" && t.length > 0) return t;
            }
        } catch (e) {}
        return "";
    }

    function IsSubtitleOccupied(sub) {
        var cls = ["ForWorld", "ForEntity"];
        for (var i = 0; i < cls.length; i++) {
            try {
                var labels = sub.FindChildrenWithClassTraverse
                    ? sub.FindChildrenWithClassTraverse(cls[i])
                    : null;
                if (labels && labels.length > 0) {
                    var t = labels[0].text;
                    if (typeof t === "string" && t.length > 0) return true;
                }
            } catch (e) {}
        }
        return false;
    }

    var DW_PRODUCT_NAME = "Deadworks";
    var _serverVersion = "";
    var _versionBanner = null;

    function VersionBannerText() {
        return _serverVersion ? (DW_PRODUCT_NAME + " " + _serverVersion) : DW_PRODUCT_NAME;
    }

    function SetServerVersion(v) {
        v = (v == null) ? "" : String(v);
        if (v === _serverVersion) return;
        _serverVersion = v;
        if (_versionBanner) {
            try { _versionBanner.text = VersionBannerText(); } catch (e) {}
        }
    }

    function StyleVersionBanner(lbl) {
        try {
            lbl.hittest = false;
            lbl.hittestchildren = false;
            var s = lbl.style;
            s.horizontalAlign = "right";
            s.verticalAlign = "bottom";
            s.marginRight = "10px";
            s.marginBottom = "38px";
            s.textAlign = "right";
            s.fontFamily = "sans";
            s.fontWeight = "semi-bold";
            s.fontSize = "15px";
            s.color = "#5FE69E55";
            s.textShadow = "1px 1px 1px black";
        } catch (e) {}
    }

    function EnsureVersionBanner() {
        var connected = (_sessionToken !== null);

        if (_versionBanner) {
            var alive = false;
            try { alive = !_versionBanner.IsValid || _versionBanner.IsValid(); } catch (e) { alive = false; }
            if (alive) {
                try {
                    if (_versionBanner.visible !== connected) _versionBanner.visible = connected;
                    var want = VersionBannerText();
                    if (connected && _versionBanner.text !== want) _versionBanner.text = want;
                } catch (e) {}
                return;
            }
            _versionBanner = null;
        }

        if (!connected) return;

        var root = FindHudRoot();
        if (!root) return;

        var lbl = null;
        try { lbl = root.FindChildTraverse("DWVersionBanner"); } catch (e) {}
        if (!lbl) {
            try { lbl = $.CreatePanel("Label", root, "DWVersionBanner"); }
            catch (e) { $.Msg(DW_TAG, " version banner create failed: ", String(e)); return; }
        }
        try { lbl.text = VersionBannerText(); } catch (e) {}
        StyleVersionBanner(lbl);
        _versionBanner = lbl;
    }

    function RouteMessage(text) {
        var sepIdx = text.indexOf(SEP);
        if (sepIdx === -1) {
            return DispatchToCatchAll(text);
        }
        var panelId = text.substring(0, sepIdx);
        var rest = text.substring(sepIdx + 1);

        var opSepIdx = rest.indexOf(SEP);
        var op = opSepIdx === -1 ? rest : rest.substring(0, opSepIdx);
        var rawAfterOp = opSepIdx === -1 ? "" : rest.substring(opSepIdx + 1);

        if (op !== "s" && op !== "h") {
            $.Msg(DW_TAG, " op '", op, "' -> panel '", panelId, "' (", rawAfterOp.length, " chars)");
        }

        if (op === "b") {
            handleBuild(panelId, decodeBuildPayloadWithStyles(rawAfterOp));
            return;
        }
        if (op === "p") {
            handlePrecache(panelId, decodeBuildPayloadWithStyles(rawAfterOp));
            return;
        }
        if (op === "o") {
            handleShow(panelId);
            return;
        }
        if (op === "x") {
            handleLoadXml(panelId, rawAfterOp);
            return;
        }
        if (op === "d") {
            handleDestroy(panelId);
            return;
        }
        if (op === "a") {
            handleAppend(panelId, rawAfterOp);
            return;
        }
        if (op === "e") {
            handleErase(panelId, rawAfterOp);
            return;
        }
        if (op === "h") {
            handleHeartbeat(rawAfterOp);
            return;
        }

        var args;
        if (op === "s") {
            var dec = lz77Decompress(rawAfterOp);
            args = dec.length > 0 ? dec.split(SEP) : [];
        } else {
            args = rawAfterOp.length > 0 ? rawAfterOp.split(SEP) : [];
        }

        var entry = _panels[panelId];
        if (!entry) {
            WarnThrottled("nopanel:" + panelId,
                "buffering ops for panel '" + panelId + "' (no layout yet)");
            entry = {
                id: panelId, cfg: {}, host: null, state: {},
                ready: false, wrapper: null, findCache: null
            };
            _panels[panelId] = entry;
        }

        var cfg = entry.cfg;
        switch (op) {
            case "s":
                var changedKeys = [];
                for (var i = 0; i + 1 < args.length; i += 2) {
                    var k = args[i];
                    entry.state[k] = args[i + 1];
                    changedKeys.push(k);
                }
                if (entry.codeDriven && !entry.host) {
                    WarnThrottled("nohost:" + panelId,
                        "panel '" + panelId + "' has state but no host — Show missing?");
                }
                applyAutoBind(entry, changedKeys);
                if (entry.xmlHost) pushAttributes(entry, changedKeys);
                runRender(entry, changedKeys);
                break;
            case "c":
                clearAutoBind(entry);
                entry.state = {};
                if (entry.xmlHost) pushAttributes(entry, null);
                runRender(entry, null);
                break;
            case "r":
                if (entry.xmlHost) pushRaw(entry, args.length > 0 ? args[0] : "");
                if (typeof cfg.onRaw === "function") {
                    try { cfg.onRaw(entry.wrapper, args.length > 0 ? args[0] : ""); }
                    catch (e) { $.Msg(DW_TAG, " onRaw '", panelId, "' threw: ", String(e)); }
                }
                break;
            default:
                $.Msg(DW_TAG, " unknown op '", op, "' for panel '", panelId, "'");
        }
    }

    function unplaceholder(v) {
        return v === "~" ? "" : v;
    }

    var UNESCAPE_MAP = {
        "A": "<",
        "B": ">",
        "C": "&",
        "D": "\x1d",
        "E": " ",
        "F": "\t",
        "G": "\n",
        "H": "\v",
        "I": "\f",
        "J": "\r"
    };

    function unescapeWire(s) {
        if (typeof s !== "string" || s.indexOf("\x1d") === -1) return s;
        var out = "";
        var i = 0;
        var n = s.length;
        while (i < n) {
            var c = s.charAt(i);
            if (c === "\x1d" && i + 1 < n) {
                var mapped = UNESCAPE_MAP[s.charAt(i + 1)];
                if (mapped !== undefined) {
                    out += mapped;
                    i += 2;
                    continue;
                }
                i++;
                continue;
            }
            out += c;
            i++;
        }
        return out;
    }

    function lz77Decompress(input) {
        if (typeof input !== "string" || input.length === 0) return input;
        var ESCAPE = "\x1e";
        var BASE = 94;
        var BASE_START = 33;
        var MIN_MATCH = 4;
        var out = "";
        var i = 0;
        var n = input.length;
        while (i < n) {
            var c = input.charAt(i);
            if (c === ESCAPE) {
                if (i + 1 < n && input.charAt(i + 1) === ESCAPE) {
                    out += ESCAPE;
                    i += 2;
                } else if (i + 3 < n) {
                    var o1 = input.charCodeAt(i + 1) - BASE_START;
                    var o2 = input.charCodeAt(i + 2) - BASE_START;
                    var lenCode = input.charCodeAt(i + 3) - BASE_START;
                    var offset = o1 * BASE + o2 + 1;
                    var length = lenCode + MIN_MATCH;
                    var startPos = out.length - offset;
                    for (var k = 0; k < length; k++) {
                        out += out.charAt(startPos + k);
                    }
                    i += 4;
                } else {
                    i++;
                }
            } else {
                out += c;
                i++;
            }
        }
        return out;
    }

    var TOKEN_MAP = {
        "A": "horizontal-align",
        "B": "vertical-align",
        "C": "text-align",
        "D": "flow-children",
        "E": "margin",
        "F": "margin-left",
        "G": "margin-right",
        "H": "margin-top",
        "I": "margin-bottom",
        "J": "padding",
        "K": "padding-left",
        "L": "padding-right",
        "M": "padding-top",
        "N": "padding-bottom",
        "O": "background-color",
        "P": "color",
        "Q": "border",
        "R": "border-radius",
        "S": "font-size",
        "T": "font-family",
        "U": "font-weight",
        "V": "width",
        "W": "height",
        "X": "visibility",
        "Y": "display",
        "Z": "overflow",
        "a": "center",
        "b": "left",
        "c": "right",
        "d": "top",
        "e": "bottom",
        "f": "solid",
        "g": "dashed",
        "h": "100%",
        "i": "1px solid",
        "j": "menuClick",
        "k": "Button ",
        "l": "Label",
        "m": "'Retail Demo', 'Noto Sans', sans-serif",
        "n": "Consolas, 'Courier New', monospace",
        "o": "letter-spacing",
        "p": "fit-children",
        "q": "white-space",
        "r": "transparent",
        "s": "box-shadow",
        "t": "min-height",
        "u": "max-height",
        "v": "transition",
        "w": "min-width",
        "x": "max-width",
        "y": "transform",
        "z": "absolute",
        "0": "relative",
        "1": "preserve",
        "2": "inherit",
        "3": "stretch",
        "4": "nowrap",
        "5": "normal",
        "6": "auto",
        "7": "bold",
        "8": "none",
        "9": "file://{images}/"
    };

    function expandTokens(s) {
        if (typeof s !== "string" || s.length === 0 || s.indexOf("^") === -1) return s;
        return s.replace(/\^([A-Za-z0-9])/g, function (m, ch) {
            var t = TOKEN_MAP[ch];
            return t !== undefined ? t : m;
        });
    }

    function resolveStyle(raw, styleTable) {
        if (typeof raw !== "string" || raw.length === 0 || raw === "~") return "";
        if (raw.charAt(0) === "@") {
            var idx = parseInt(raw.substring(1), 10);
            if (!isNaN(idx) && idx >= 0 && styleTable && idx < styleTable.length) {
                return styleTable[idx];
            }
            return "";
        }
        return raw;
    }

    function parseTree(fields, startIdx, styleTable) {
        if (startIdx + 6 > fields.length) return null;
        var node = {
            t: fields[startIdx + 0],
            i: unplaceholder(fields[startIdx + 1]),
            s: resolveStyle(fields[startIdx + 2], styleTable),
            x: unplaceholder(fields[startIdx + 3]),
            e: unplaceholder(fields[startIdx + 4]),
            c: []
        };
        var count = parseInt(fields[startIdx + 5], 10);
        if (isNaN(count) || count < 0) count = 0;
        var nextIdx = startIdx + 6;
        for (var k = 0; k < count; k++) {
            var sub = parseTree(fields, nextIdx, styleTable);
            if (!sub) return null;
            node.c.push(sub.node);
            nextIdx = sub.nextIdx;
        }
        return { node: node, nextIdx: nextIdx };
    }

    function decodeBuildPayload(rawAfterOp) {
        var lzExpanded = lz77Decompress(rawAfterOp);
        var detokenized = expandTokens(lzExpanded);
        return detokenized.length > 0 ? detokenized.split(SEP) : [];
    }

    function decodeBuildPayloadWithStyles(rawAfterOp) {
        var all = decodeBuildPayload(rawAfterOp);
        if (!all || all.length === 0) return null;
        var n = parseInt(all[0], 10);
        if (isNaN(n) || n < 0) return null;
        if (all.length < 1 + n) return null;
        return {
            styleTable: all.slice(1, 1 + n),
            fields: all.slice(1 + n)
        };
    }

    function ensurePanelEntry(panelId) {
        var entry = _panels[panelId];
        if (!entry) {
            entry = {
                id: panelId, cfg: {}, host: null, state: {},
                ready: false, wrapper: null, findCache: null, codeDriven: true
            };
            _panels[panelId] = entry;
        }
        if (!entry.codeDriven && _registrations[panelId]) {
            WarnThrottled("collide:" + panelId,
                "server sent a layout for '" + panelId + "', which this client " +
                "registers itself — the server's tree wins; rename one of them");
        }
        entry.codeDriven = true;
        return entry;
    }

    function handlePrecache(panelId, payload) {
        if (!payload || !payload.fields || payload.fields.length < 6) {
            $.Msg(DW_TAG, " precache: not enough fields for '", panelId, "' (got ",
                (payload && payload.fields ? payload.fields.length : 0), ")");
            return;
        }
        var parsed = parseTree(payload.fields, 0, payload.styleTable);
        if (!parsed) {
            $.Msg(DW_TAG, " precache: tree parse failed for '", panelId, "'");
            return;
        }
        _cachedTrees[panelId] = parsed.node;
        ensurePanelEntry(panelId);
        $.Msg(DW_TAG, " precached '", panelId, "' (",
            payload.styleTable.length, " styles, ", payload.fields.length, " fields)");
    }

    function handleShow(panelId) {
        var tree = _cachedTrees[panelId];
        if (!tree) {
            $.Msg(DW_TAG, " show: no precached layout for '", panelId, "' (call /precache first)");
            return;
        }

        var entry = ensurePanelEntry(panelId);

        teardownHost(entry);

        var hud = FindHudRoot();
        if (!hud) { $.Msg(DW_TAG, " show: no HUD root yet for '", panelId, "'"); return; }
        entry.host = $.CreatePanel("Panel", hud, "DWHost_" + panelId);
        try { entry.host.style.width = "100%"; } catch (e) {}
        try { entry.host.style.height = "100%"; } catch (e) {}
        try { entry.host.hittest = false; } catch (e) {}

        entry.findCache = null;
        entry.wrapper = makeWrapper(entry);
        buildNode(entry, entry.host, tree);
        entry.ready = true;
        runInit(entry);

        var replayed = applyAutoBind(entry, null);
        runRender(entry, null);

        var built = -1;
        try { built = entry.host.GetChildCount(); } catch (e) {}
        $.Msg(DW_TAG, " shown '", panelId, "' (root=", tree.t, " children=", built,
              " replayed=", replayed, ")");
    }

    function handleBuild(panelId, payload) {
        handlePrecache(panelId, payload);
        var entry = _panels[panelId];
        if (entry) entry.state = {};
        handleShow(panelId);
    }

    function buildNode(entry, parent, node) {
        if (!node || typeof node !== "object") return;
        var elem = null;
        var t = node.t;
        var id = node.i || "";
        var hasChildren = Array.isArray(node.c) && node.c.length > 0;
        switch (t) {
            case "p":
                elem = $.CreatePanel("Panel", parent, id);
                break;
            case "v":
                elem = $.CreatePanel("Panel", parent, id);
                try { elem.style.flowChildren = "down"; } catch (e) {}
                break;
            case "h":
                elem = $.CreatePanel("Panel", parent, id);
                try { elem.style.flowChildren = "right"; } catch (e) {}
                break;
            case "L":
                elem = $.CreatePanel("Label", parent, id);
                if (typeof node.x === "string") {
                    try { elem.text = node.x; } catch (e) {}
                }
                break;
            case "I":
                elem = $.CreatePanel("Image", parent, id);
                if (typeof node.x === "string" && node.x.length > 0) {
                    try { elem.SetImage(node.x); } catch (e) {
                        $.Msg(DW_TAG, " SetImage failed for '", id, "' src='", node.x, "': ", String(e));
                    }
                }
                break;
            case "B":
                elem = $.CreatePanel("Button", parent, id);
                if (!hasChildren && typeof node.x === "string" && node.x.length > 0) {
                    var inner = $.CreatePanel("Label", elem, "");
                    try { inner.text = node.x; } catch (e) {}
                }
                if (typeof node.e === "string" && node.e.length > 0) {
                    var parts = node.e.split(",");
                    var evtName = parts[0];
                    var evtArgs = parts.slice(1);
                    (function (ev, ea) {
                        elem.SetPanelEvent("onactivate", function () {
                            entry.wrapper.send.apply(entry.wrapper, [ev].concat(ea));
                        });
                    })(evtName, evtArgs);
                }
                break;
            default:
                $.Msg(DW_TAG, " unknown node type '", String(t), "'");
                return;
        }
        if (typeof node.s === "string" && node.s.length > 0) applyStyle(elem, node.s);
        if (hasChildren) {
            for (var i = 0; i < node.c.length; i++) buildNode(entry, elem, node.c[i]);
        }
    }

    function applyStyle(panel, styleStr) {
        if (!panel) return;
        var parts = styleStr.split(";");
        for (var i = 0; i < parts.length; i++) {
            var p = parts[i];
            var idx = p.indexOf(":");
            if (idx === -1) continue;
            var k = p.substring(0, idx).replace(/^\s+|\s+$/g, "");
            var v = p.substring(idx + 1).replace(/^\s+|\s+$/g, "");
            if (!k || !v) continue;
            var camel = k.replace(/-([a-z])/g, function (_, c) { return c.toUpperCase(); });
            try { panel.style[camel] = v; } catch (e) {}
        }
    }

    function handleDestroy(panelId) {
        var entry = _panels[panelId];
        if (!entry) return;
        teardownHost(entry);
        delete _panels[panelId];
    }

    function DestroyAllPanels() {
        for (var id in _panels) {
            if (!Object.prototype.hasOwnProperty.call(_panels, id)) continue;
            teardownHost(_panels[id]);
        }
        _panels = {};
        _cachedTrees = {};
        _inflight = {};
        ReviveRegistered();
    }

    function handleHeartbeat(rawAfterOp) {
        var parts = rawAfterOp.split(SEP);
        var token = parts[0];
        _lastDwActivityMs = Date.now();
        var changed = (_sessionToken !== null && _sessionToken !== token);
        _sessionToken = token;
        if (changed) {
            $.Msg(DW_TAG, " session changed — clearing stale UI");
            DestroyAllPanels();
        }

        SetServerVersion(parts.length > 2 ? parts[2] : "");
        if (parts.length > 3) SetCaptionLinger(parts[3]);

        var hello = false;
        for (var i = 3; i < parts.length; i++) {
            if (parts[i] === "a") hello = true;
        }
        if (hello) {
            try { $.DispatchEvent("CitadelConCommand", "dw_ui ~|ack|" + token + "|" + AckPanelList()); }
            catch (e) { $.Msg(DW_TAG, " handshake ack failed: ", String(e)); }
        }
    }

    var ACK_LIST_MAX = 200;

    function AckPanelList() {
        var out = [];
        var used = 0;
        for (var id in _registrations) {
            if (!Object.prototype.hasOwnProperty.call(_registrations, id)) continue;
            if (id.indexOf("|") !== -1 || id.indexOf(",") !== -1) {
                WarnThrottled("badid:" + id,
                    "panel id '" + id + "' contains '|' or ',' — not announced to the server");
                continue;
            }
            if (used + id.length + 1 > ACK_LIST_MAX) {
                WarnThrottled("acktrunc", "too many registered panels to announce; " +
                    "listing the first " + out.length);
                break;
            }
            used += id.length + 1;
            out.push(id);
        }
        return out.join(",");
    }

    function handleAppend(panelId, rawAfterOp) {
        var sepIdx = rawAfterOp.indexOf(SEP);
        if (sepIdx === -1) {
            $.Msg(DW_TAG, " append: missing parentId for '", panelId, "'");
            return;
        }
        var parentId = rawAfterOp.substring(0, sepIdx);
        var compressed = rawAfterOp.substring(sepIdx + 1);

        var entry = _panels[panelId];
        if (!entry || !entry.host) {
            $.Msg(DW_TAG, " append: no host for '", panelId, "' — call Build/Show first");
            return;
        }
        var parent = lookup(entry, parentId);
        if (!parent) {
            $.Msg(DW_TAG, " append: parent '", parentId, "' not found in '", panelId, "'");
            return;
        }

        var payload = decodeBuildPayloadWithStyles(compressed);
        if (!payload || !payload.fields || payload.fields.length < 6) {
            $.Msg(DW_TAG, " append: subtree parse failed for '", panelId, "'");
            return;
        }
        var parsed = parseTree(payload.fields, 0, payload.styleTable);
        if (!parsed) {
            $.Msg(DW_TAG, " append: tree parse failed for '", panelId, "'");
            return;
        }

        if (!entry.wrapper) entry.wrapper = makeWrapper(entry);
        buildNode(entry, parent, parsed.node);
    }

    function handleErase(panelId, targetId) {
        if (!targetId) return;
        var entry = _panels[panelId];
        if (!entry || !entry.host) return;
        var target = lookup(entry, targetId);
        if (target) {
            try { target.DeleteAsync(0); } catch (e) {}
        }
        if (entry.findCache) delete entry.findCache[targetId];
        if (entry.state) delete entry.state[targetId];
    }

    function handleLoadXml(panelId, xmlPath) {
        if (!xmlPath) {
            $.Msg(DW_TAG, " loadxml: missing path for '", panelId, "'");
            return;
        }
        var entry = ensurePanelEntry(panelId);

        teardownHost(entry);

        var hud = FindHudRoot();
        if (!hud) { $.Msg(DW_TAG, " loadxml: no HUD root yet for '", panelId, "'"); return; }

        var host = $.CreatePanel("Panel", hud, "DWHost_" + panelId);
        try { host.style.width = "100%"; } catch (e) {}
        try { host.style.height = "100%"; } catch (e) {}
        try { host.hittest = false; } catch (e) {}

        var ok = false;
        try { ok = host.BLoadLayout(xmlPath, false, false); }
        catch (e) { $.Msg(DW_TAG, " loadxml BLoadLayout threw: ", String(e)); }
        if (!ok) {
            $.Msg(DW_TAG, " loadxml failed for '", xmlPath, "' — reporting to server");
            reportAddon(panelId, "fail");
            try { host.DeleteAsync(0); } catch (e) {}
            return;
        }
        reportAddon(panelId, "ok");

        entry.host = host;
        entry.findCache = null;
        entry.xmlHost = true;
        entry.wakeForm = undefined;
        entry.attrSeq = 0;
        entry.rawSeq = 0;
        entry.wrapper = makeWrapper(entry);
        entry.ready = true;
        runInit(entry);

        applyAutoBind(entry, null);
        pushAttributes(entry, null);
        runRender(entry, null);

        $.Msg(DW_TAG, " loaded xml '", xmlPath, "' for panel '", panelId, "'");
    }

    function DispatchToCatchAll(text) {
        for (var i = 0; i < _userHandlers.length; i++) {
            try { _userHandlers[i](text); }
            catch (e) { $.Msg(DW_TAG, " onUser handler threw: ", String(e)); }
        }
    }

    var WARN_THROTTLE_MS = 5000;
    var _warnAt = {};

    function WarnThrottled(key, msg) {
        var now = Date.now();
        if (_warnAt[key] && (now - _warnAt[key]) < WARN_THROTTLE_MS) return;
        _warnAt[key] = now;
        $.Msg(DW_TAG, " ", msg);
    }

    var LOG_SUMMARY_MS = 2000;
    var _msgCount = 0;
    var _msgBytes = 0;
    var _lastMsgLogMs = 0;

    function NoteMessage(len) {
        _msgCount++;
        _msgBytes += len;
        var now = Date.now();
        if (_lastMsgLogMs === 0) { _lastMsgLogMs = now; return; }
        var dt = now - _lastMsgLogMs;
        if (dt < LOG_SUMMARY_MS) return;
        $.Msg(DW_TAG, " ", _msgCount, " msgs / ", _msgBytes, " chars in ", dt, "ms (",
              Math.round(_msgBytes * 1000 / dt), " chars/s)");
        _msgCount = 0;
        _msgBytes = 0;
        _lastMsgLogMs = now;
    }

    function HandleUserMessage(wireText) {
        var text = unescapeWire(wireText);
        if (text.charAt(0) !== "~") NoteMessage(text.length);
        RouteMessage(text);
    }

    var _inflight = {};

    var _recentChunks = [];
    var DEDUP_WINDOW_MS = 500;

    function isDuplicateChunk(rest) {
        var now = Date.now();
        while (_recentChunks.length > 0 && now - _recentChunks[0].time > DEDUP_WINDOW_MS) {
            _recentChunks.shift();
        }
        for (var i = 0; i < _recentChunks.length; i++) {
            if (_recentChunks[i].rest === rest) return true;
        }
        return false;
    }

    function rememberChunk(rest) {
        _recentChunks.push({ rest: rest, time: Date.now() });
    }

    function ProcessUserSubtitle(rest) {
        if (rest.length < 2) return;
        _lastDwActivityMs = Date.now();
        if (isDuplicateChunk(rest)) return;
        rememberChunk(rest);
        var id = rest.charAt(0);
        var flag = rest.charAt(1);
        var payload = rest.substring(2);

        switch (flag) {
            case "*":
                HandleUserMessage(payload);
                return;
            case "+":
                _inflight[id] = payload;
                return;
            case "=":
                if (_inflight[id] === undefined) return;
                _inflight[id] += payload;
                return;
            case "!":
                var full = (_inflight[id] !== undefined ? _inflight[id] : "") + payload;
                delete _inflight[id];
                HandleUserMessage(full);
                return;
            default:
                $.Msg(DW_TAG, " unknown flag '", flag, "' for id '", id, "'");
                return;
        }
    }

    var _pollTick = 0;
    var _dumpedTree = false;
    var _lastLiveFlag = false;

    var CAPTION_POOL = 6;
    var _poolForeign = 0;
    var _starvedSinceMs = 0;
    var MAX_STARVE_CREDIT_MS = 30000;

    function CheckHeartbeatWatchdog() {
        var now = Date.now();
        var pollGap = _lastWatchdogMs ? (now - _lastWatchdogMs) : 0;
        _lastWatchdogMs = now;

        if (_sessionToken === null) return;

        if (pollGap > STALL_GAP_MS) {
            $.Msg(DW_TAG, " UI stalled ", pollGap, "ms — not counting it against the heartbeat");
            _lastDwActivityMs = now;
            return;
        }

        if (pollGap > 600) {
            WarnThrottled("pollgap", "slow UI frame: " + pollGap + "ms between polls" +
                " (captions live ~200ms — items can expire unseen)");
        }

        if (_poolForeign < CAPTION_POOL) _starvedSinceMs = 0;

        if (now - _lastDwActivityMs <= HEARTBEAT_TIMEOUT_MS) return;

        if (_poolForeign >= CAPTION_POOL) {
            if (_starvedSinceMs === 0) _starvedSinceMs = now;
            if (now - _starvedSinceMs < MAX_STARVE_CREDIT_MS) {
                WarnThrottled("starved",
                    "caption pool full of game captions (" + _poolForeign + "/" + CAPTION_POOL +
                    ") — crediting the gap instead of tearing down");
                _lastDwActivityMs = now;
                return;
            }
            $.Msg(DW_TAG, " caption pool starved for ", MAX_STARVE_CREDIT_MS,
                  "ms — giving up and treating it as a disconnect");
        }
        _starvedSinceMs = 0;

        $.Msg(DW_TAG, " no heartbeat for ", HEARTBEAT_TIMEOUT_MS, "ms — tearing down UI");

        try { $.DispatchEvent("CitadelConCommand", "dw_ui ~|resync|" + _sessionToken); }
        catch (e) { $.Msg(DW_TAG, " resync request failed: ", String(e)); }

        _sessionToken = null;
        DestroyAllPanels();
    }

    function ScanSubtitles() {
        var phase = "start";
        try {
            phase = "find";
            var container = FindSubtitles();
            if (!container) return false;

            phase = "count";
            var n = container.GetChildCount();
            if (n > 0) HideSubtitles(container);

            var occupied = 0, dwSlots = 0;
            for (var i = 0; i < n; i++) {
                phase = "getchild " + i;
                var sub = container.GetChild(i);
                if (!sub) continue;

                phase = "gettext " + i;
                var text = GetSubtitleText(sub);

                phase = "census " + i;
                var isDw = !!text && text.indexOf(USER_PREFIX) !== -1;
                if (isDw) dwSlots++;
                if (isDw || !!text || IsSubtitleOccupied(sub)) occupied++;

                if (!text) continue;
                if (sub.dwLastText === text) continue;
                sub.dwLastText = text;

                phase = "prefix " + i;
                var start = text.indexOf(USER_PREFIX);
                if (start === -1) continue;

                phase = "process " + i;
                var rest = text.substring(start + USER_PREFIX.length);
                ProcessUserSubtitle(rest);
            }
            _poolForeign = occupied - dwSlots;
        } catch (e) {
            $.Msg(DW_TAG, " ScanSubtitles error phase=", phase, " err=", String(e));
        }
        return true;
    }

    function PollSubtitles() {
        _pollTick++;
        try {
            var ccEvery = (_sessionToken === null) ? CC_ASSERT_TICKS_WAITING : CC_ASSERT_TICKS_RUNNING;
            if (_pollTick % ccEvery === 0) EnsureCaptionsEnabled();

            EnsureVersionBanner();

            CheckHeartbeatWatchdog();

            var live = (_sessionToken !== null);
            if (live !== _lastLiveFlag) {
                _lastLiveFlag = live;
                for (var id in _panels) {
                    if (!Object.prototype.hasOwnProperty.call(_panels, id)) continue;
                    if (_panels[id].xmlHost) pushAttributes(_panels[id], []);
                }
            }

            if (!ScanSubtitles() && (_pollTick === 1 || _pollTick % 60 === 0)) {
                $.Msg(DW_TAG, " no subtitles container yet (tick=", _pollTick, ")");
            }
        } catch (e) {
            $.Msg(DW_TAG, " PollSubtitles error err=", String(e));
        }
        $.Schedule(POLL_SEC, PollSubtitles);
    }

    var _rescanQueued = false;

    function OnCaptionItemsChanged() {
        ScanSubtitles();
        if (_rescanQueued) return;
        _rescanQueued = true;
        $.Schedule(0, function () {
            _rescanQueued = false;
            ScanSubtitles();
        });
    }

    try {
        $.RegisterForUnhandledEvent("ClosedCaptionItemsChanged", OnCaptionItemsChanged);
        $.Msg(DW_TAG, " subscribed to ClosedCaptionItemsChanged");
    } catch (e) {
        $.Msg(DW_TAG, " ClosedCaptionItemsChanged unavailable, polling only: ", String(e));
    }

    $.Schedule(0.5, PollSubtitles);
})();
