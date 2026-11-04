# Changelog

Formato baseado em [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/)
e [Versionamento Semântico](https://semver.org/lang/pt-BR/).

## [Não lançado]

### Adicionado
- Solution `Mimir.slnx` com Core, Infrastructure, Cli e testes.
- `KnowledgeAssistant`, que depende apenas de `IChatClient`.
- `ChatClientFactory` com os provedores Ollama (padrão), Anthropic, OpenAI e Fake.
- CLI para fazer perguntas pelo terminal.
- 14 testes unitários, sem rede e sem custo.
- Pipeline de CI no GitHub Actions.
- ADR 0001 — provedor configurável; ADR 0002 — fine-tuning em Python.
