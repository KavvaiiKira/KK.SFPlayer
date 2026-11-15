(() => {
    "use strict";

    let player;
    let sdkReady = false;
    let configuration;
    let tokenRequest = 0;
    let volume = 0.1;
    let progressTimer;
    let stateGeneration = 0;
    let transportQueue = Promise.resolve();
    let pendingTransports = 0;
    let closed = false;
    let audioContext;
    let silentSource;
    const callbacks = new Map();
    const send = message => window.chrome.webview.postMessage(message);
    const clampVolume = value => Math.max(0, Math.min(1, Number.isFinite(value) ? value : 0.1));

    function initializeAudio() {
        try {
            audioContext = new window.AudioContext();
            silentSource = audioContext.createConstantSource();
            silentSource.offset.value = 0;
            silentSource.connect(audioContext.destination);
            silentSource.start();
            audioContext.resume().catch(() => {});
        } catch {
            // Spotify playback remains available if idle audio initialization fails.
        }
        send({ type: "loaded" });
    }

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
            originalTrackUri: state.track_window?.current_track?.linked_from?.uri || null,
            contextUri: state.context?.uri || null,
            trackName: state.track_window?.current_track?.name || null,
            artists: state.track_window?.current_track?.artists?.map(artist => artist.name).join(", ") || null
        });
    }

    function refreshState() {
        if (closed || pendingTransports > 0) return Promise.resolve();
        const generation = stateGeneration;
        return player.getCurrentState().then(state => {
            if (!closed && pendingTransports === 0 && generation === stateGeneration) publishState(state);
        }).catch(() => {});
    }

    function enqueueTransport(action) {
        pendingTransports++;
        stateGeneration++;
        transportQueue = transportQueue.then(async () => {
            if (!closed) await action();
        }).catch(() => {
            if (!closed) send({ type: "error", code: "playback_error" });
        }).finally(() => {
            pendingTransports--;
            stateGeneration++;
        });
        transportQueue.then(() => {
            if (!closed && pendingTransports === 0) refreshState();
        });
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
        player.addListener("player_state_changed", state => {
            stateGeneration++;
            publishState(state);
        });
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
            enqueueTransport(() => player.togglePlay());
        } else if (data.type === "next" && player) {
            enqueueTransport(() => player.nextTrack());
        } else if (data.type === "previous" && player) {
            enqueueTransport(() => player.previousTrack());
        } else if (data.type === "seek" && player) {
            enqueueTransport(() => player.seek(Math.max(0, Math.floor(Number(data.position) || 0))));
        } else if (data.type === "volume") {
            volume = clampVolume(data.volume);
            if (player) player.setVolume(volume).catch(() => send({ type: "error", code: "playback_error" }));
        }
    });
    window.addEventListener("pagehide", () => {
        closed = true;
        stateGeneration++;
        callbacks.clear();
        clearInterval(progressTimer);
        if (player) player.disconnect();
        if (silentSource) silentSource.stop();
        if (audioContext) audioContext.close().catch(() => {});
    });
    initializeAudio();
})();
