"use strict";

(function () {
    var TAG = "[DW_ADDON]";
    var ATTR_VAL = "dwv_";
    var ATTR_SYS = "dw_";
    var HOST_PREFIX = "DWHost_";

    var POLL_SEC = 0.05;

    var _host = null;
    var _cfg = null;
    var _api = null;
    var _panelId = "";
    var _state = {};
    var _local = {};
    var _seq = "";
    var _first = true;
    var _generation = 0;
    var _findCache = {};
    var _sawServer = false;
    var _ctxWasHost = false;
    var _rawSeq = "";
    var _live = "0";
    var _source = "none";

    var PROTO = 1;
    var _protoWarned = false;
    var WIRE_SEP = String.fromCharCode(31);

    function attrSys(name, dflt) { return readAttr(ATTR_SYS + name, dflt); }
    function attrVal(name, dflt) { return readAttr(ATTR_VAL + name, dflt); }

    function readAttr(name, dflt) {
        if (dflt === undefined) dflt = "";
        if (!_host) return dflt;
        try { return _host.GetAttributeString(name, dflt); }
        catch (e) { return dflt; }
    }

    function splitList(s) {
        return (typeof s === "string" && s.length > 0) ? s.split(",") : [];
    }

    function findHost(start) {
        var p = start, depth = 0;
        while (p && depth < 12) {
            var id = "";
            try { id = p.id || ""; } catch (e) {}
            if (id.indexOf(HOST_PREFIX) === 0) return p;
            try { p = p.GetParent(); } catch (e) { break; }
            depth++;
        }
        return start;
    }

    function hostPanelId(host) {
        var id = "";
        try { id = host.id || ""; } catch (e) {}
        if (id.indexOf(HOST_PREFIX) === 0) return id.substring(HOST_PREFIX.length);
        return attrSys("id", "");
    }

    function lookup(id) {
        if (!_host) return null;
        var hit = _findCache[id];
        if (hit !== undefined) {
            var alive = false;
            try { alive = !hit.IsValid || hit.IsValid(); } catch (e) { alive = false; }
            if (alive) return hit;
            delete _findCache[id];
        }
        var el = null;
        try { el = _host.FindChildTraverse(id); } catch (e) {}
        if (el) _findCache[id] = el;
        return el;
    }

    function makeApi() {
        function on(id, eventName, fn) {
            var el = lookup(id);
            if (el && el.SetPanelEvent) {
                try { el.SetPanelEvent(String(eventName), fn); return true; }
                catch (e) { $.Msg(TAG, " on('", id, "','", eventName, "') failed: ", String(e)); }
            }
            return false;
        }
        return {
            host: _host,
            panelId: _panelId,
            find: lookup,
            on: on,
            onClick: function (id, fn) { return on(id, "onactivate", fn); },
            text: function (id, value) {
                var el = lookup(id);
                if (!el) return false;
                try { el.text = (value == null) ? "" : String(value); return true; }
                catch (e) { return false; }
            },
            get: function (key, fallback) {
                var v = _state[String(key)];
                if (v !== undefined) return v;
                return (fallback === undefined) ? "" : fallback;
            },
            num: function (key, fallback) {
                var n = parseFloat(_state[String(key)]);
                return isFinite(n) ? n : (fallback === undefined ? 0 : fallback);
            },
            int: function (key, fallback) {
                var n = parseInt(_state[String(key)], 10);
                return isFinite(n) ? n : (fallback === undefined ? 0 : fallback);
            },
            bool: function (key, fallback) {
                var v = _state[String(key)];
                if (v === undefined) return fallback === undefined ? false : fallback;
                v = String(v).toLowerCase();
                if (v === "1" || v === "true" || v === "yes" || v === "on") return true;
                if (v === "0" || v === "false" || v === "no" || v === "off" || v === "") return false;
                return fallback === undefined ? false : fallback;
            },
            set: function (key, value) {
                var k = String(key);
                _state[k] = (value == null) ? "" : String(value);
                _local[k] = true;
                emit([k]);
            },
            refresh: function () { emit(null); },
            state: function () { return _state; },
            send: function (eventName) {
                var id = _panelId || hostPanelId(_host);
                if (!id) { $.Msg(TAG, " send dropped: panel id still unknown"); return; }
                var parts = [id, String(eventName)];
                for (var i = 1; i < arguments.length; i++) parts.push(String(arguments[i]));
                try { $.DispatchEvent("CitadelConCommand", "dw_ui " + parts.join("|")); }
                catch (e) { $.Msg(TAG, " send failed: ", String(e)); }
            },
            connected: function () { return _live === "1"; },
            diagnose: function () {
                if (!_host) return "no host";
                if (!_sawServer) return "no data";
                if (_live !== "1") return "no session";
                return "";
            },
            debug: debugInfo
        };
    }

    function debugInfo() {
        var hid = "";
        try { hid = _host ? (_host.id || "(unnamed)") : "(null)"; } catch (e) { hid = "(err)"; }
        return {
            hostId: hid,
            panelId: _panelId,
            ctxWasHost: _ctxWasHost,
            source: _source,
            seq: _seq,
            live: _live,
            keys: (function () { var n = 0; for (var k in _state) n++; return n; })()
        };
    }

    function emit(changed) {
        if (!_cfg || typeof _cfg.render !== "function") return;
        try { _cfg.render(_api, _state, changed || null); }
        catch (e) { $.Msg(TAG, " render threw: ", String(e)); }
    }

    function reconcile(all) {
        var present = {};
        for (var i = 0; i < all.length; i++) present[all[i]] = true;
        for (var k in _state) {
            if (!Object.prototype.hasOwnProperty.call(_state, k)) continue;
            if (present[k] || _local[k]) continue;
            delete _state[k];
        }
    }

    function readWire() {
        var el = lookup("DWWire");
        if (!el) return null;
        var t = "";
        try { t = el.text || ""; } catch (e) { return null; }
        if (!t) return null;
        var f = t.split(WIRE_SEP);
        if (f.length < 4) return null;
        var map = {}, ks = [];
        for (var i = 4; i + 1 < f.length; i += 2) { map[f[i]] = f[i + 1]; ks.push(f[i]); }
        return {
            src: "label", seq: f[0], live: f[1], id: f[2], changed: f[3], keys: ks,
            value: function (k) { return map[k] !== undefined ? map[k] : ""; }
        };
    }

    function readUpdate() {
        var seq = attrSys("seq", "");
        if (seq !== "") {
            return {
                src: "attr", seq: seq,
                live: attrSys("live", "0"),
                id: attrSys("id", ""),
                changed: attrSys("changed", ""),
                keys: splitList(attrSys("keys", "")),
                value: function (k) { return attrVal(k, ""); }
            };
        }
        return readWire();
    }

    function pump() {
        var u = readUpdate();
        if (!u || u.seq === "" || u.seq === _seq) return false;
        _seq = u.seq;
        _live = u.live;
        _source = u.src;

        if (!_protoWarned) {
            var theirs = attrSys("proto", "");
            if (theirs !== "" && theirs !== String(PROTO)) {
                _protoWarned = true;
                $.Msg(TAG, " PROTOCOL MISMATCH: this dw_addon.js speaks v", PROTO,
                      ", the bootstrap speaks v", theirs,
                      ". You are running a copy of dw_addon.js from an addon VPK",
                      " rather than the framework's — remove it.");
            }
        }

        if (!_sawServer) {
            _sawServer = true;
            if (!_panelId && u.id) {
                _panelId = u.id;
                if (_api) _api.panelId = _panelId;
            }
            $.Msg(TAG, " channel live via ", u.src, " for '", _panelId, "' at seq ", u.seq);
        }

        var changed = (_first || u.changed === "") ? null : splitList(u.changed);
        _first = false;

        var read = changed || u.keys;
        for (var i = 0; i < read.length; i++) _state[read[i]] = u.value(read[i]);
        reconcile(u.keys);

        emit(changed);
        return true;
    }

    function pumpRaw() {
        var rs = attrSys("rawseq", "");
        if (rs === "" || rs === _rawSeq) return false;
        _rawSeq = rs;
        if (_cfg && typeof _cfg.onRaw === "function") {
            try { _cfg.onRaw(_api, attrSys("raw", "")); }
            catch (e) { $.Msg(TAG, " onRaw threw: ", String(e)); }
        }
        return true;
    }

    function tick(gen) {
        if (gen !== _generation) return;
        pump();
        pumpRaw();
        $.Schedule(POLL_SEC, function () { tick(gen); });
    }

    function reportReady() {
        if (!_panelId) return;
        try { $.DispatchEvent("CitadelConCommand", "dw_ui ~|addon|" + _panelId + "|ready"); }
        catch (e) { $.Msg(TAG, " ready report failed: ", String(e)); }
    }

    globalThis.DWAddon = {
        register: function (cfg) {
            if (!cfg) { $.Msg(TAG, " register requires a config object"); return; }
            _cfg = cfg;

            var ctx = cfg.root || $.GetContextPanel();
            if (!ctx) { $.Msg(TAG, " no context panel — cannot register"); return; }
            _host = findHost(ctx);
            _ctxWasHost = (ctx === _host);
            _panelId = hostPanelId(_host);

            _findCache = {};
            _api = makeApi();

            if (cfg.layout) {
                $.Msg(TAG, " ignoring `layout` — a runtime addon already is its own layout");
            }
            if (cfg.id && _panelId && cfg.id !== _panelId) {
                $.Msg(TAG, " ignoring id '", cfg.id, "': the server addressed this panel as '",
                      _panelId, "'");
            }

            _rawSeq = attrSys("rawseq", "");

            try { _host.SetPanelEvent("onactivate", function () { pump(); pumpRaw(); return true; }); }
            catch (e) { $.Msg(TAG, " wake channel unavailable, polling only: ", String(e)); }

            if (typeof cfg.init === "function") {
                try { cfg.init(_api); }
                catch (e) { $.Msg(TAG, " init threw: ", String(e)); }
            }

            var hostId = "?";
            try { hostId = _host.id || "(unnamed)"; } catch (e) {}
            $.Msg(TAG, " registered: host='", hostId, "' panelId='", _panelId,
                  "' ctxWasHost=", (ctx === _host));

            reportReady();

            pump();
            tick(++_generation);
        },

        shutdown: function () {
            _generation++;
            if (_cfg && typeof _cfg.onDestroy === "function") {
                try { _cfg.onDestroy(_api); }
                catch (e) { $.Msg(TAG, " onDestroy threw: ", String(e)); }
            }
        },

        connected: function () { return _live === "1"; },
        state: function () { return _state; },
        debug: debugInfo
    };

    if (!globalThis.DW) {
        globalThis.DW = {
            registerPanel: function (cfg) { globalThis.DWAddon.register(cfg); },
            connected: function () { return _live === "1"; },
            serverVersion: function () { return _state.ver || ""; },
            registeredPanels: function () { return _panelId ? [_panelId] : []; },
            log: function () {
                var args = ["[DW_ADDON]"];
                for (var i = 0; i < arguments.length; i++) args.push(arguments[i]);
                $.Msg.apply($, args);
            }
        };
    }
})();
