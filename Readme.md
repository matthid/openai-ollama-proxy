# Start

Set Environment variables 

- `OPENAI_BASE_URL`
- `OPENAI_API_KEY`

Then run the proxy with

`dotnet run`


Now setup your tools against localhost:4222 (ollama)
And you can use all models which are available at the given openai-compatible endpoint.
I recommend using [litellm](https://github.com/BerriAI/litellm) and routing everything through it.