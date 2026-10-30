# feat/streaming-upload: send the recording while the key is held (v1, for review)

The PR7 plan's item 5, which its v2 rulings (`2026-10-28-latency.md`) made conditional: built **only if measurement shows upload time matters**.

## The measurement (2026-10-28, live, OpenAI, warm connection)
`LiveUploadShareTests` uses an 8 KB socket send buffer, so "body sent" tracks the wire and not the kernel buffer. The first attempt, with the default buffer, understated it badly (0–1 ms for 108 KB).

| Clip | Bytes | Body on the wire | First byte | Upload share |
|---|---|---|---|---|
| 3.4 s | 108 KB | 35–45 ms | 560–710 ms | about 6% |
| 30 s | 960 KB | 200–245 ms | 840–1155 ms | about 22% |

About 70 ms on a 10 s dictation and 200 ms on a 30 s one, against a 1 s Dictate target. That matters, so this gets built.

**Live check of the streamed form** (`LiveStreamedUploadProbe`): a chunked multipart body, with a WAV header of unknown length (`0xFFFFFFFF`), written in real time at 100 ms every 100 ms.
- `whisper-1` / `verbose_json`: 200, duration 3.37 s for 3.38 s of audio, and the transcript ends correctly.
- `gpt-4o-transcribe` / `json`: 200, and the transcript ends correctly.
- The response arrived 630–740 ms after the body ended, which is server time only.
- No 411. Groq and custom endpoints aren't verified (there's no Groq key here), so they keep the batch upload.

## Binding constraints (v2 rulings), and how each is met
1. **The request opens only once the hold is committed and the target isn't a password field. Audio is buffered until then.**
   - The pipeline creates the STT session after the 300 ms mark, when the hold is a dictation, and after the snapshot, so a password field has already been refused by then.
   - The session opens nothing before its first speech chunk (constraint 2).
   - Ruling, recorded: "committed" is read as "a dictation, past the tap window", not "key released". Read the second way, nothing would overlap the speech, and the feature would be pointless.
2. **It starts at the first speech chunk, with about 250 ms of pre-roll.** "Speech" uses SilenceTrim's judgement, half the speech threshold. The pre-roll is `SilenceTrim.LeadPadMs` (400 ms), so streamed and batch audio start the same way.
3. **A discard, Esc or NoSpeech aborts the request through its token, and never completes the body.**
   - Disposing the session without completing it cancels the in-flight request. The multipart never gets its closing boundary, so the server has no complete file to transcribe.
   - The pipeline already disposes the session on every path.
4. **The live test checks the transcribed duration and checks for 411.** The probe becomes a permanent live test:
   - `whisper-1` / `verbose_json` duration within 5% of the audio;
   - `gpt-4o-transcribe` text that ends right;
   - never a 411.
5. **Its own byte and time ceiling.**
   - 6 minutes of audio, the 5-minute hands-free cap plus margin. That's about 11.5 MB at 32 KB/s.
   - Past either ceiling the stream is aborted, and `CompleteAsync` uses the batch path.
6. **"It runs over HTTP/2, or it doesn't warm."** The shared client is HTTP/1.1, so while streaming is on, the key-down warm-up skips the STT host:
   - the stream opens its own connection during speech, so the handshake overlaps the talking;
   - a warmed connection would be one more open socket for nothing.
   The Claude host is still warmed.

## Design
**`StreamingTranscriptionSession : ITranscriptionSession`** (Core):
- **Local copy.** It keeps every PCM byte locally, for the batch fallback (Retry's WAV is written by the pipeline, as now).
- **`PushAudio`:**
  - Before the first speech chunk, it only buffers.
  - On it, it starts `SendAsync` with a body fed from a bounded channel: the header, the pre-roll, then each chunk as it's pushed.
  - Pushes never block the capture loop. If the channel is full (the network stalls), the stream is abandoned for the batch path.
- **`CompleteAsync`:**
  - **No stream started** (no speech): the batch request, which is the same as today.
  - **Stream started:** it completes the body and awaits the response.
  - **When it falls back to batch with the full local audio:**
    - a network error, a timeout or a pinning error before any response;
    - a 400, 411, 413 or 415, which may be about the streamed form;
    - an abandoned stream.
  - **When it doesn't fall back** (it reports the error as the batch path would): 401, 403, 429 or 5xx, because a second upload wouldn't change those answers.
- **`DisposeAsync` without completing** cancels the request.
- **Results:** `UploadBytes` is the bytes actually streamed. `BodySentMs` is the time the body completed, measured from `CompleteAsync`, at about key-up.

**The factory** returns a streaming session when `settings.StreamingUpload` is on (default **true**) and the provider is OpenAI. Otherwise it returns the batch session.

**`RunTiming.SttUpload`:** an enum, `Batch`, `Streamed` or `StreamFellBack`, so the timing report can compare the two. It's content-free.

**Settings:** `streamingUpload` in settings.json, documented in the README. There's no UI toggle; it's a fallback switch, not a preference.

## Tests (TDD, fake handler that reads the body as it arrives)
- No request is sent before the first speech chunk. Silence only, then complete, gives a single batch request.
- The stream starts at the first speech chunk and carries the header, 400 ms of pre-roll and every later chunk, in order, while the session is still open.
- Dispose before complete cancels the request, and the body never ends with the closing boundary.
- A network error before any response falls back to a batch request with the full audio, and the result is reported as `StreamFellBack`. A 400 or 411 also falls back. A 401 or 429 doesn't, and maps exactly as batch does.
- Past the byte ceiling, the stream is abandoned, and complete uses batch.
- A slow consumer (a stalled network) never blocks `PushAudio`.
- The warm-up skips the STT host when streaming is on, and the Claude host is unchanged.
- The factory picks streaming only for OpenAI with the setting on.
- No content in the logs: the new enum is covered by the existing test.

**E2E:** the Sandbox has no network and uses a fake STT, so streaming isn't exercised end to end there. The fake-handler unit tests and the live tests cover it, and the PR says so.

## v2 rulings (three reviews: HTTP correctness, privacy and product, architecture and tests)

These replace the matching parts above where they differ.

1. **Default off. It's Bryan's call, put to him as one line.** All three reviews note that streaming changes what leaves the machine. When a hold that passed 300 ms is then discarded, by Esc, another key or a click, a switch away, locking, or no speech, the audio already spoken has reached OpenAI's servers. The request is aborted before it's complete, so nothing is transcribed, but it can't be recalled. The gain is about 70 ms on a 10 s dictation and about 200 ms on a 30 s one. So `streamingUpload` defaults to **false**, gets a checkbox in Settings > Advanced with one honest sentence, and the README states the trade.
2. **The stream starts only when the speech gate hears speech:** the same `SpeechGate` as NoSpeech, at the full threshold for 300 ms, not one frame at half the threshold. So a cough, a click or room noise doesn't open a request. Starting later loses nothing, because the body begins from the local buffer.
3. **Streamed audio is byte-for-byte what `SilenceTrim.Trim` would send.**
   - Frames sit on Trim's 100 ms grid from the start of the audio.
   - The start offset follows Trim's rule: if the first sound is at least 10 frames in, start 4 frames before it; otherwise start at 0.
   - Silent frames after the last sound are held locally and sent only when sound resumes.
   - At Complete, the held tail gets Trim's rule: if it's 1 s or more, send a 1 s pad; otherwise send all of it, including the partial frame.
   - The test checks byte equality with `Trim(allPcm)`, pushed in chunk sizes that aren't multiples of the frame. Trailing hallucinations from silence don't come back.
4. **The body is served from the local buffer by offset**, not from a channel it consumes.
   - It can be serialised again if the pool resends on a fresh connection.
   - The writer waits on a signal and never blocks `PushAudio`.
   - The buffer is chunk-list based, under a lock.
5. **An abort really is an abort.**
   - The content overrides `SerializeToStreamAsync(stream, context, ct)` and uses `ct` for every wait.
   - Dispose and abandon cancel the session's token *first*, then fault the feed, so the serializer **throws** and the closing boundary and the terminating chunk are never written.
   - The in-flight send is cancelled at once on abandon, and awaited for at most a short bound before any fallback, so two uploads never overlap.
   - A loopback test with a real `SocketsHttpHandler` (plain http, raw handler, no pinning) asserts that the server never sees the terminating chunk after a dispose.
6. **The fallback is only for failures before the body completed.**
   - A `BodyCompleted` flag is set when the closing boundary is written. After it, a network error or timeout is reported the way batch reports it, never re-uploaded, so there's no double transcription or billing.
   - Before it, these fall back to batch:
     - a network error;
     - the handler's connect timeout;
     - a 400, 408, 411, 413 or 415 response.
   - Everything else is reported as batch reports it, with no fallback: a pinning failure (`InvalidEndpoint`), 401, 403, 429, 5xx, 3xx, 404, 409 and 422.
   - The caller's token is linked to the stream. A cancel tap or the pipeline's `SttTimeout` aborts at once as the caller's cancel. A fallback runs inside the same remaining budget.
   - The endpoint (host, model and key) is captured at `Create` and reused for the fallback.
7. **Retry and anything else that pushes a finished buffer use batch.** `SttSessionOptions.Live` is true only on the pipeline's capture path.
8. **Shared code by composition.**
   - Request building, `MapStatus`, `ErrorCode`, `ParseText`, failure logging and `TimedContent` (with the `ct` overload) move into an internal helper used by both sessions.
   - The streaming session wraps a batch session for the local copy, the trim and the fallback.
   - Batch's existing status, pinning and no-content tests become a theory over both kinds.
9. **Accounting.**
   - `SttResult.Upload` and `SttException.Upload` carry `Batch`, `Streamed` or `StreamFellBack`. `RunTiming.SttUpload` is nullable, since re-runs have no STT.
   - On a fallback, the timings include the time lost on the failed stream, and `UploadBytes` is the batch's.
   - `NewConnection` stays as measured.
   - The timing report splits release-to-paste, `SttBodySentMs`, `SttDoneMs` and cold connections by `SttUpload`.
10. **Logs (content-free):**
    - `StreamFellBack` (reason tag);
    - `StreamAborted` (bytes sent, and whether a discard caused it).
    So "how often did a cancelled hold send audio" can be read from the logs. Background faults are always observed and mapped without inner exceptions.
11. **Warm-up.** A single shared predicate, "streaming applies" (setting on, OpenAI, a live session), is used by both the factory and the warmer. The warmer skips only OpenAI's STT host, and only when streaming applies. Groq and custom endpoints are still warmed. The reason is restated: the handshake overlaps speech anyway.
12. **Tests added:**
    - a fake handler that reads the body incrementally through `CopyToAsync(stream, ct)`, with byte-threshold signals and a flush count; there's a flush after each chunk;
    - pipeline-level: on every discard, NoSpeech, cancel, capture-failure and interrupted path, the session is disposed and never completed (tracked by `FakeStt`);
    - an early 401 or 400 arriving mid-speech;
    - 408;
    - a caller cancel gives no batch request;
    - the time ceiling, with an injected `TimeProvider`;
    - a push after Complete is ignored;
    - Retry gives `Batch`;
    - an OpenAI pass of the no-content test, including a fallback and an early error;
    - the unknown-length WAV header moves into `WavFile`, with a unit test.
13. **Live tests:**
    - The session itself streams the 3.4 s clip in real time. A test-only option asks for `verbose_json`, and the duration must be within 5%. There must be no 411.
    - A 60 s real-time stream (the noise fixture looped) must complete and transcribe.
    - The verified models (`gpt-4o-transcribe`, `whisper-1`) are named in a code comment and in the README.
14. **README:**
    - the Esc and cancel bullets get the streaming clause;
    - "What is sent where" gets "as you speak, if streaming is on (OpenAI only), from the first speech in a hold past 300 ms";
    - `streamingUpload` is documented.
15. **Found in the live tests: idle bodies are dropped.**
    - OpenAI drops a request body that has sent nothing for 15 s or more. 10 s survived; 5, 10, 15 and 20 s pauses were probed.
    - Held-back silence makes exactly such gaps, so 30 s and 60 s holds with long pauses failed their streams and fell back.
    - Ruling: after 8 s with nothing new to send, the stream is **parked**. It is torn down like a discard (its body never ends, logged `StreamParked`), and when speech resumes, a fresh request re-sends from the start. That's a re-upload of the audio so far, about 0.2 s per MB on this uplink.
    - Trim equivalence and the privacy rule are unchanged: nothing is sent that the batch upload wouldn't send.
    - A hold that ends in the parked silence is sent as one batch request at key-up, which is today's behaviour.
    - Live tests confirm:
      - a 20 s pause mid-hold streams;
      - a minute of continuous speech (1.9 MB) streams in one request, so there's no duration limit at that length;
      - `gpt-4o-transcribe` and `whisper-1` transcribe a streamed hold whole.
16. **The fallback log** carries the HTTP status, or the exception's type and HResult (never its message), so a failure in the field can be diagnosed.
17. **Not done:** the `verbose_json` duration check is replaced by "the last words are transcribed", which proves the tail was heard; the probe that preceded this verified the duration (3.37 s for 3.38 s of audio).

