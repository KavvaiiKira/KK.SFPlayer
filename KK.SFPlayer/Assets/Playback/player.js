(() => {
    "use strict";

    let player;
    let sdkReady = false;
    let configuration;
    let tokenRequest = 0;
    let volume = 0.1;
    let progressTimer;
    let seekGeneration = 0;
    const callbacks = new Map();
    const send = message => window.chrome.webview.postMessage(message);
    const clampVolume = value => Math.max(0, Math.min(1, Number.isFinite(value) ? value : 0.1));

    function publishState(state) {
        if (!state) return;
        const duration = Math.max(0, Math.floor(state.duration || 0));
        send({
            type: "state", playing: !state.paused,
            shuffle: !!state.shuffle,
            repeatMode: Number.isInteger(state.repeat_mode) ? state.repeat_mode : 0,
            position: Math.max(0, Math.min(duration, Math.floor(state.position || 0))),
            duration,
            canSeek: duration > 0 && !state.disallows?.seeking,
            trackUri: state.track_window?.current_track?.uri || null,
            contextUri: state.context?.uri || null,
            trackName: state.track_window?.current_track?.name || null,
            artists: state.track_window?.current_track?.artists?.map(artist => artist.name).join(", ") || null
        });
    }

    function refreshState() {
        const generation = seekGeneration;
        return player.getCurrentState().then(state => {
            if (generation === seekGeneration) publishState(state);
        }).catch(() => {});
    }

    function connect() {
        if (!sdkReady || !configuration || player) return;
        player = new Spotify.Player({
            name: configuration.name,
            volume,
            enableMediaSession: true,
            getOAuthToken: callback => {
                const id = ++tokenRequest;
                callbacks.set(id, callback);
                send({ type: "token", id });
            }
        });
        for (const type of ["initialization_error", "authentication_error", "account_error", "playback_error"]) {
            player.addListener(type, () => send({ type: "error", code: type }));
        }
        player.addListener("ready", ({ device_id }) => {
            clearInterval(progressTimer);
            progressTimer = setInterval(refreshState, 1000);
            send({ type: "ready", deviceId: device_id });
        });
        player.addListener("not_ready", () => send({ type: "error", code: "not_ready" }));
        player.addListener("player_state_changed", publishState);
        player.addListener("autoplay_failed", () => send({ type: "error", code: "playback_error" }));
        player.connect().then(connected => {
            if (!connected) send({ type: "error", code: "initialization_error" });
        }).catch(() => send({ type: "error", code: "initialization_error" }));
    }

    window.onSpotifyWebPlaybackSDKReady = () => {
        sdkReady = true;
        connect();
    };
    window.chrome.webview.addEventListener("message", ({ data }) => {
        if (data.type === "connect") {
            configuration = data;
            volume = clampVolume(data.volume);
            connect();
        } else if (data.type === "token") {
            const callback = callbacks.get(data.id);
            callbacks.delete(data.id);
            if (callback) callback(data.token);
        } else if (data.type === "toggle" && player) {
            player.togglePlay().catch(() => send({ type: "error", code: "playback_error" }));
        } else if (data.type === "next" && player) {
            player.nextTrack().catch(() => send({ type: "error", code: "playback_error" }));
        } else if (data.type === "previous" && player) {
            player.previousTrack().catch(() => send({ type: "error", code: "playback_error" }));
        } else if (data.type === "seek" && player) {
            seekGeneration++;
            player.seek(Math.max(0, Math.floor(Number(data.position) || 0)))
                .then(refreshState).catch(() => send({ type: "error", code: "playback_error" }));
        } else if (data.type === "volume") {
            volume = clampVolume(data.volume);
            if (player) player.setVolume(volume).catch(() => send({ type: "error", code: "playback_error" }));
        }
    });
    window.addEventListener("pagehide", () => {
        callbacks.clear();
        clearInterval(progressTimer);
        if (player) player.disconnect();
    });
    send({ type: "loaded" });
})();
