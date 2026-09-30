# Confiabilidade

Orders usa EF Bus Outbox no POST /orders. As definições dos consumidores de negócio habilitam separadamente EF Consumer Outbox/Inbox. Registrar tabelas Outbox não habilita esse middleware. A transação confirma estado de negócio, Inbox e mensagens de saída juntos; a publicação ocorre depois. Referência: [MassTransit Outbox](https://masstransit.massient.com/configuration/middleware/outbox).

Payment.OrderId e SimulatedCharges.OrderId são únicos no SQL. Inbox suprime MessageId repetido durante sua janela; as chaves duráveis protegem o negócio além dela. Inserções concorrentes recuperam o resultado vencedor. Reutilizar chave com outro valor é erro permanente. Orders executa um UPDATE SQL condicionado a Pending.

## Gateways

Cada gateway singleton tem pipeline Polly próprio, de fora para dentro: Retry → Circuit Breaker → Timeout. Defaults: duas retentativas, jitter exponencial desde 200ms, timeout 1s/tentativa; janela 30s, mínimo quatro tentativas, limiar 50%, abertura 10s. Os callbacks passam o token do Polly ao trabalho, conforme a [estratégia de timeout](https://www.pollydocs.org/strategies/timeout.html).

Somente indisponibilidade e timeout são retentados. Essas falhas e circuito aberto podem acionar fallback. Antes, consulta-se o registro para reconciliar uma conclusão ambígua. Recusa comercial é resultado durável; cancelamento original e validações permanentes são propagados. Erros SQL causados pelo cancelamento são normalizados para preservar a semântica do token.

O registro usa conexão e transação EF independentes. Seu INSERT confirmado **é** a cobrança simulada; não há efeito financeiro posterior. Os gateways compartilham esse registro. Isso não garante cobrança única com provedores reais independentes, que exigem idempotência do provedor, reconciliação de resultados desconhecidos e política explícita de liquidação.

Retries do consumidor são limitados (1s, 3s, 5s) para falhas SQL transitórias e disputas de índices únicos. Erros permanentes ficam na fila _error e nos logs; inspecione antes de reprocessar. SQL indisponível impede novos pedidos; Outbox desacopla a disponibilidade do broker.

O gateway ainda mantém aberta a transação do consumidor. O laboratório usa atrasos limitados e uma instância; alta vazão exige avaliação própria. Contadores descrevem tentativas/transições observadas, sem substituir o registro contábil.
