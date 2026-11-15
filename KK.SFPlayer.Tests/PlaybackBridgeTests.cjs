const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");
const settle = () => new Promise(resolve => setImmediate(resolve));

const source = fs.readFileSync(path.join(__dirname, "../KK.SFPlayer/Assets/Playback/player.js"), "utf8");

function createBridge(connected = true) {
    const messages = [];
    const listeners = {};
    const players = [];
    const timers = new Set();
    const audio = { started: 0, stopped: 0, closed: 0, resumed: 0, offset: null };
    const window = {
        AudioContext: class {
            destination = {};
            createConstantSource() {
                const source = {
                    offset: { value: 1 },
                    connect: () => { audio.offset = source.offset.value; },
                    start: () => { audio.started++; },
                    stop: () => { audio.stopped++; }
                };
                return source;
            }
            resume() { audio.resumed++; return Promise.resolve(); }
            close() { audio.closed++; return Promise.resolve(); }
        },
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
            this.nextCount = 0;
            this.previousCount = 0;
            this.state = { paused: false, position: 0, duration: 180000, track_window: { current_track: { uri: "spotify:track:one" } } };
            players.push(this);
        }
        addListener(name, callback) { this.listeners[name] = callback; }
        connect() { return Promise.resolve(connected); }
        disconnect() { this.disconnected = true; }
        togglePlay() {
            this.paused = !this.paused;
            this.state.paused = this.paused;
            this.listeners.player_state_changed({ paused: this.paused });
            return Promise.resolve();
        }
        setVolume(volume) { this.volume = volume; return Promise.resolve(); }
        nextTrack() { this.nextCount++; return Promise.resolve(); }
        previousTrack() { this.previousCount++; return Promise.resolve(); }
        getCurrentState() { return Promise.resolve(this.state); }
        seek(position) { this.state.position = position; return Promise.resolve(); }
    }
    vm.runInNewContext(source, {
        window, Spotify: { Player },
        setInterval: callback => { timers.add(callback); return callback; },
        clearInterval: timer => timers.delete(timer)
    });
    return {
        window, players, messages, timers, audio,
        send: data => listeners.message({ data }),
        close: () => listeners.pagehide()
    };
}

test("idle audio uses the playback WebView without authorizing or starting Spotify", () => {
    const bridge = createBridge();
    assert.equal(bridge.audio.started, 1);
    assert.equal(bridge.audio.resumed, 1);
    assert.equal(bridge.audio.offset, 0);
    assert.equal(bridge.players.length, 0);
    assert.equal(bridge.messages.some(message => message.type === "token"), false);
    bridge.close();
    assert.equal(bridge.audio.stopped, 1);
    assert.equal(bridge.audio.closed, 1);
});

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

test("pause and resume report actual player state", async () => {
    const bridge = createBridge();
    bridge.send({ type: "connect", name: "KK.SFPlayer" });
    bridge.window.onSpotifyWebPlaybackSDKReady();
    bridge.send({ type: "toggle" });
    await settle();
    assert.equal(bridge.messages.at(-1).type, "state");
    assert.equal(bridge.messages.at(-1).playing, false);
    bridge.send({ type: "toggle" });
    await settle();
    assert.equal(bridge.messages.at(-1).playing, true);
});

test("next and previous control the local SDK player and modes come from its state", async () => {
    const bridge = createBridge();
    bridge.send({ type: "connect", name: "KK.SFPlayer" });
    bridge.window.onSpotifyWebPlaybackSDKReady();
    bridge.send({ type: "next" });
    bridge.send({ type: "previous" });
    await settle();
    const player = bridge.players[0];
    assert.equal(player.nextCount, 1);
    assert.equal(player.previousCount, 1);
    player.listeners.player_state_changed({
        paused: false, shuffle: true, repeat_mode: 2,
        track_window: { current_track: { uri: "spotify:track:changed", name: "New track", artists: [{ name: "Artist" }] } },
        context: { uri: "spotify:playlist:current" }
    });
    assert.equal(bridge.messages.at(-1).shuffle, true);
    assert.equal(bridge.messages.at(-1).repeatMode, 2);
    assert.equal(bridge.messages.at(-1).trackUri, "spotify:track:changed");
    assert.equal(bridge.messages.at(-1).contextUri, "spotify:playlist:current");
    assert.equal(bridge.messages.at(-1).trackName, "New track");
    assert.equal(bridge.messages.at(-1).artists, "Artist");
});

test("progress polls local SDK state, supports seek and stops polling when closed", async () => {
    const bridge = createBridge();
    bridge.send({ type: "connect", name: "KK.SFPlayer" });
    bridge.window.onSpotifyWebPlaybackSDKReady();
    const player = bridge.players[0];
    player.listeners.ready({ device_id: "local-device" });
    assert.equal(bridge.timers.size, 1);
    player.state.position = 30000;
    await [...bridge.timers][0]();
    assert.equal(bridge.messages.at(-1).position, 30000);
    assert.equal(bridge.messages.at(-1).duration, 180000);
    assert.equal(bridge.messages.at(-1).canSeek, true);
    bridge.send({ type: "seek", position: 90000 });
    await settle();
    assert.equal(player.state.position, 90000);
    assert.equal(bridge.messages.at(-1).position, 90000);
    player.state.paused = true;
    player.state.disallows = { seeking: true };
    await [...bridge.timers][0]();
    assert.equal(bridge.messages.at(-1).playing, false);
    assert.equal(bridge.messages.at(-1).canSeek, false);
    bridge.close();
    assert.equal(bridge.timers.size, 0);
});

test("a delayed progress poll cannot overwrite a newer track event", async () => {
    const bridge = createBridge();
    bridge.send({ type: "connect", name: "KK.SFPlayer" });
    bridge.window.onSpotifyWebPlaybackSDKReady();
    const player = bridge.players[0];
    player.listeners.ready({ device_id: "local-device" });
    let completePoll;
    player.getCurrentState = () => new Promise(resolve => { completePoll = resolve; });
    const pending = [...bridge.timers][0]();
    player.listeners.player_state_changed({
        paused: false, position: 0, duration: 180000,
        track_window: { current_track: { uri: "spotify:track:new" } }
    });
    completePoll(player.state);
    await pending;
    assert.equal(bridge.messages.at(-1).trackUri, "spotify:track:new");
});

test("transport commands wait for each other and stale polls cannot roll a skip back", async () => {
    const bridge = createBridge();
    bridge.send({ type: "connect", name: "KK.SFPlayer" });
    bridge.window.onSpotifyWebPlaybackSDKReady();
    const player = bridge.players[0];
    player.listeners.ready({ device_id: "local-device" });
    let finishPoll;
    player.getCurrentState = () => new Promise(resolve => { finishPoll = resolve; });
    const poll = [...bridge.timers][0]();
    let finishNext;
    const calls = [];
    player.nextTrack = () => {
        calls.push("next");
        return new Promise(resolve => { finishNext = resolve; });
    };
    player.previousTrack = () => { calls.push("previous"); return Promise.resolve(); };
    bridge.send({ type: "next" });
    bridge.send({ type: "previous" });
    await settle();
    assert.deepEqual(calls, ["next"]);
    player.listeners.player_state_changed({
        paused: false, position: 100, duration: 180000,
        track_window: { current_track: { uri: "spotify:track:new" } }
    });
    finishPoll(player.state);
    await poll;
    assert.equal(bridge.messages.at(-1).trackUri, "spotify:track:new");
    finishNext();
    await settle();
    assert.deepEqual(calls, ["next", "previous"]);
    bridge.close();
});

test("a rejected transport command does not block following commands", async () => {
    const bridge = createBridge();
    bridge.send({ type: "connect", name: "KK.SFPlayer" });
    bridge.window.onSpotifyWebPlaybackSDKReady();
    const player = bridge.players[0];
    player.nextTrack = () => Promise.reject(new Error("failed"));
    bridge.send({ type: "next" });
    bridge.send({ type: "previous" });
    await settle();
    assert.equal(player.previousCount, 1);
    assert.equal(bridge.messages.filter(message => message.type === "error").length, 1);
});

test("closing the page cancels queued transport commands", async () => {
    const bridge = createBridge();
    bridge.send({ type: "connect", name: "KK.SFPlayer" });
    bridge.window.onSpotifyWebPlaybackSDKReady();
    bridge.send({ type: "next" });
    bridge.close();
    await settle();
    assert.equal(bridge.players[0].nextCount, 0);
});

test("track relinking preserves the original playlist track identity", () => {
    const bridge = createBridge();
    bridge.send({ type: "connect", name: "KK.SFPlayer" });
    bridge.window.onSpotifyWebPlaybackSDKReady();
    bridge.players[0].listeners.player_state_changed({
        paused: false, position: 0, duration: 180000,
        track_window: { current_track: { uri: "spotify:track:regional", linked_from: { uri: "spotify:track:original" } } }
    });
    assert.equal(bridge.messages.at(-1).trackUri, "spotify:track:regional");
    assert.equal(bridge.messages.at(-1).originalTrackUri, "spotify:track:original");
});

test("current album artwork is published with playback state", () => {
    const bridge = createBridge();
    bridge.send({ type: "connect", name: "KK.SFPlayer" });
    bridge.window.onSpotifyWebPlaybackSDKReady();
    bridge.players[0].listeners.player_state_changed({
        paused: false, duration: 180000,
        track_window: { current_track: { uri: "spotify:track:one", album: { images: [{ url: "https://example.com/cover" }] } } }
    });
    assert.equal(bridge.messages.at(-1).artworkUrl, "https://example.com/cover");
});
