# C# Voice Agent Starter

A C# starter application that demonstrates how to build a voice agent using Deepgram's Voice Agent API. This application provides a web interface where users can interact with an AI voice agent through their microphone.

## What is Deepgram?

[Deepgram's](https://deepgram.com/) voice AI platform provides APIs for speech-to-text, text-to-speech, and full speech-to-speech voice agents. Over 200,000+ developers use Deepgram to build voice AI products and features.

## Sign-up to Deepgram

Before you start, it's essential to generate a Deepgram API key to use in this project. [Sign-up now for Deepgram and create an API key](https://console.deepgram.com/signup?jump=keys).

## Prerequisites

- .NET 8.0 SDK or later
- A Deepgram API key
- A microphone for voice input

## Quickstart

Follow these steps to get started with this C# voice agent starter application.

### Clone the repository

1. Go to Github and [clone the repository](https://github.com/deepgram-starters/csharp-voice-agent)

2. Set your Deepgram API key:
```bash
export DEEPGRAM_API_KEY=your_api_key_here
```

### Run the application

The application will start a web server on port 3000. Once running, you can [access the application in your browser](http://localhost:3000/).

```bash
dotnet run Program.cs
```

- Allow microphone access when prompted.
- Speak into your microphone to interact with the Deepgram Voice Agent.
- You should hear the agent's responses played back in your browser.

### Using the `app-requirements.mdc` File

1. Clone or Fork this repo.
2. Modify the `app-requirements.mdc`
3. Add the necessary configuration settings in the file.
4. You can refer to the MDC file used to help build this starter application by reviewing  [app-requirements.mdc](.cursor/rules/app-requirements.mdc)


## Testing

Test the application with:

```bash
@TODO
```

## Getting Help

We love to hear from you so if you have questions, comments or find a bug in the project, let us know! You can either:

- [Open an issue in this repository](https://github.com/deepgram-starters/csharp-voice-agent/issues/new)
- [Join the Deepgram Github Discussions Community](https://github.com/orgs/deepgram/discussions)
- [Join the Deepgram Discord Community](https://discord.gg/deepgram)

## Contributing

We welcome contributions! Please see our [Contributing Guidelines](./CONTRIBUTING.md) for details.

## Security

For security concerns, please see our [Security Policy](./SECURITY.md).

## Code of Conduct

Please see our [Code of Conduct](./CODE_OF_CONDUCT.md) for community guidelines.

## Author

[Deepgram](https://deepgram.com)

## License

This project is licensed under the MIT license. See the [LICENSE](./LICENSE) file for more info.
