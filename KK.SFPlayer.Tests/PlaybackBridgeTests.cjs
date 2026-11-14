const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");

const source = fs.readFileSync(path.join(__dirname, "../KK.SFPlayer/Assets/Playback/player.js"), "utf8");

function createBridge(connected = true) {
    const messages = [];
    const listeners = {};
    const players = [];
    const window = {
        chrome: { webview: {
            postMessage: message => messages.push(message),
            addEventListener: (name, callback) => { listeners[name] = callback; }
        } },
        addEventListener: (name, callback) => { listeners[name] = callback; }
    };
    class Player {
        constructor(options) {
            this.options = options;
            this.listeners = {};
            this.disconnected = false;
            this.paused = false;
            this.volume = options.volume;
            players.push(this);
        }
        addListener(name, callback) { this.listeners[name] = callback; }
        connect() { return Promise.resolve(connected); }
        disconnect() { this.disconnected = true; }
        togglePlay() {
            this.paused = !this.paused;
            this.listeners.player_state_changed({ paused: this.paused });
            return Promise.resolve();
        }
        setVolume(volume) { this.volume = volume; return Promise.resolve(); }
    }
    vm.runInNewContext(source, { window, Spotify: { Player } });
    return {
        window, players, messages,
        send: data => listeners.message({ data }),
        close: () => listeners.pagehide()
    };
}

test("SDK and native configuration can arrive in either order without duplicate players", () => {
    for (const sdkFirst of [true, false]) {
        const bridge = createBridge();
        assert.equal(bridge.messages[0].type, "loaded");
        if (sdkFirst) bridge.window.onSpotifyWebPlaybackSDKReady();
        bridge.send({ type: "connect", name: "KK.SFPlayer" });
        if (!sdkFirst) bridge.window.onSpotifyWebPlaybackSDKReady();
        bridge.send({ type: "connect", name: "KK.SFPlayer" });
        assert.equal(bridge.players.length, 1);
        assert.equal(bridge.players[0].options.name, "KK.SFPlayer");
        assert.equal(bridge.players[0].options.enableMediaSession, true);
    }
});

test("token requests including refresh are paired with the right callback", () => {
    const bridge = createBridge();
    bridge.send({ type: "connect", name: "KK.SFPlayer" });
    bridge.window.onSpotifyWebPlaybackSDKReady();
    const values = [];
    const player = bridge.players[0];
    player.options.getOAuthToken(token => values.push(`first:${token}`));
    player.options.getOAuthToken(token => values.push(`refresh:${token}`));
    const requests = bridge.messages.filter(message => message.type === "token");
    assert.notEqual(requests[0].id, requests[1].id);
    bridge.send({ type: "token", id: requests[1].id, token: "refreshed" });
    bridge.send({ type: "token", id: requests[0].id, token: "initial" });
    bridge.send({ type: "token", id: requests[0].id, token: "duplicate" });
    assert.deepEqual(values, ["refresh:refreshed", "first:initial"]);
    assert.equal(JSON.stringify(bridge.messages).includes("refreshed"), false);
});

test("only the SDK ready device is reported to the native player", () => {
    const bridge = createBridge();
    bridge.send({ type: "connect", name: "KK.SFPlayer" });
    bridge.window.onSpotifyWebPlaybackSDKReady();
    bridge.players[0].listeners.ready({ device_id: "embedded-device" });
    assert.equal(bridge.messages.at(-1).type, "ready");
    assert.equal(bridge.messages.at(-1).deviceId, "embedded-device");
});

test("SDK errors expose codes without forwarding potentially sensitive messages", () => {
    const bridge = createBridge();
    bridge.send({ type: "connect", name: "KK.SFPlayer" });
    bridge.window.onSpotifyWebPlaybackSDKReady();
    for (const code of ["authentication_error", "account_error", "initialization_error", "playback_error", "not_ready"]) {
        bridge.players[0].listeners[code]({ message: "sensitive details" });
        assert.equal(bridge.messages.at(-1).type, "error");
        assert.equal(bridge.messages.at(-1).code, code);
        assert.equal(JSON.stringify(bridge.messages.at(-1)).includes("sensitive"), false);
    }
});

test("failed connection is reported", async () => {
    const bridge = createBridge(false);
    bridge.send({ type: "connect", name: "KK.SFPlayer" });
    bridge.window.onSpotifyWebPlaybackSDKReady();
    await Promise.resolve();
    assert.equal(bridge.messages.at(-1).code, "initialization_error");
});

test("leaving the page disconnects playback and drops pending token callbacks", () => {
    const bridge = createBridge();
    bridge.send({ type: "connect", name: "KK.SFPlayer" });
    bridge.window.onSpotifyWebPlaybackSDKReady();
    let called = false;
    bridge.players[0].options.getOAuthToken(() => { called = true; });
    const request = bridge.messages.at(-1);
    bridge.close();
    bridge.send({ type: "token", id: request.id, token: "late" });
    assert.equal(bridge.players[0].disconnected, true);
    assert.equal(called, false);
});

test("playback starts at ten percent and volume supports mute and clamping", () => {
    const bridge = createBridge();
    bridge.send({ type: "connect", name: "KK.SFPlayer", volume: 0.1 });
    bridge.window.onSpotifyWebPlaybackSDKReady();
    const player = bridge.players[0];
    assert.equal(player.volume, 0.1);
    bridge.send({ type: "volume", volume: 0 });
    assert.equal(player.volume, 0);
    bridge.send({ type: "volume", volume: 0.35 });
    assert.equal(player.volume, 0.35);
    bridge.send({ type: "volume", volume: 2 });
    assert.equal(player.volume, 1);
});

test("pause and resume report actual player state", () => {
    const bridge = createBridge();
    bridge.send({ type: "connect", name: "KK.SFPlayer" });
    bridge.window.onSpotifyWebPlaybackSDKReady();
    bridge.send({ type: "toggle" });
    assert.equal(bridge.messages.at(-1).type, "state");
    assert.equal(bridge.messages.at(-1).playing, false);
    bridge.send({ type: "toggle" });
    assert.equal(bridge.messages.at(-1).playing, true);
});
