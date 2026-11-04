# ADR 0002 — Treino de fine-tuning em Python, com o uso do modelo em C#

- **Status:** Aceita
- **Data:** 2026-11-04

## Contexto
O portfólio é em Java, C# e SQLite, mas as ferramentas de treino de modelos abertos são quase todas em Python.
O treino precisa ser gratuito, em GPU local ou em um serviço com camada grátis, como o Google Colab.

## Decisão
Isolar o treino na pasta `training/`, em Python. O resultado é exportado para o Ollama, e a aplicação em C#
usa o modelo ajustado pelo mesmo `IChatClient`, sem conhecer o processo de treino.

## Alternativas consideradas
- **Treinar em .NET** — manteria uma só linguagem, mas com poucas opções maduras para ajuste de LLMs.
- **Serviço pago de fine-tuning** — mais simples, mas fere a regra de custo zero. Fica documentado no README.
- **Não fazer fine-tuning** — sairia mais barato, mas deixaria um dos conceitos do projeto de fora.

## Consequências
- ✅ Usa a ferramenta certa para o treino sem espalhar Python pelo sistema.
- ✅ O modelo ajustado é só mais um modelo para a aplicação.
- ⚠️ A escolha do modelo depende da memória da GPU; isso será medido no Dia 13.
- ⚠️ O Colab gratuito tem limites de uso e de tempo, que podem interromper um treino.
