# Start

Set Environment variables 

- `OPENAI_BASE_URL` or `LITELLM_PROXY_API_BASE`
- `OPENAI_API_KEY` or `LITELLM_PROXY_API_KEY`

Then run the proxy with

`dotnet run`


Now setup your tools against localhost:4222 (ollama)
And you can use all models which are available at the given openai-compatible endpoint.
I recommend using [litellm](https://github.com/BerriAI/litellm) and routing everything through it.