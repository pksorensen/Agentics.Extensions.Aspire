# Agentics.Extensions.Aspire.Testing.Videos

Cinematic **video-recording helpers** for
[`Aspire.Hosting.Testing`](https://learn.microsoft.com/dotnet/aspire/testing/) — turn a Playwright
walkthrough of your distributed app into a narrated product video: TTS voiceover, burned-in
subtitles, and ffmpeg muxing.

> Published as `Agentics.Extensions.Aspire.Testing.Videos`; the API lives in the `Aspire.Hosting`
> namespace.

## Install

```bash
dotnet add package Agentics.Extensions.Aspire.Testing.Videos
```

## Requirements

- [Microsoft.Playwright](https://www.nuget.org/packages/Microsoft.Playwright) browsers installed
  (`playwright install`).
- `ffmpeg` on the host for muxing the final video.
- A TTS provider (e.g. Azure OpenAI) for the voiceover track.

Used to generate the agentics.dk product demo video from an Aspire test host. See the repository
for a worked example.

## License

MIT
