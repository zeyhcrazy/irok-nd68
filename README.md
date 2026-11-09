# iRok ND68 Pro — correção de queda USB (Full-Speed)

Estudo de caso: um teclado **iRok ND68 Pro** caía e reconectava sozinho quando ligado direto nas portas USB traseiras de uma placa **X570 (AMD)**. Só funcionava através de um hub USB. Este repositório documenta o diagnóstico e a correção, feita por engenharia reversa do firmware.

> O firmware modificado **não** está incluído aqui (é derivado do firmware oficial da iRok, com direito autoral). O repositório traz o diagnóstico, a análise e as ferramentas que escrevi. A correção está descrita abaixo para quem quiser reproduzir no próprio aparelho.

## O problema

- Nas portas AMD do X570, o teclado entrava em um laço de reset: conectava e caía em poucos segundos.
- No hub USB, funcionava normal.
- Cabos, firmware oficial e portas diferentes foram descartados como causa. Um teclado idêntico de um amigo funcionava numa placa AM5 — ou seja, incompatibilidade entre o PHY USB do teclado (chip **HPM5300**, pilha **CherryUSB**) e o controlador USB das portas-raiz do X570.

## O diagnóstico

Para provar que era queda de enumeração e não software, escrevi um monitor que registra cada conexão/desconexão USB e marca o instante exato da queda:

- `monitor/MonitorTeclado.cs` — monitor em C# que escuta os eventos de dispositivo do Windows e anota quanto tempo o teclado ficou conectado antes de cair.
- `scripts/Capturar-Queda.ps1` — inicia uma captura de trace do USB (ETW) e fecha sozinha na próxima queda, para analisar o momento da falha.
- `scripts/Estabilizar-Teclado.ps1` — ajustes de energia USB (desliga o *selective suspend*) para descartar gerenciamento de energia como causa.

## A correção

A análise do firmware mostrou que a enumeração falhava na negociação **High-Speed** (o *chirp* de 480 Mbps) nas portas-raiz do X570. A solução foi forçar o teclado a conectar em **Full-Speed (12 Mbps)**, que enumera sem o chirp:

- Na função `usb_dcd_init` (endereço `0x80032D14`) o firmware passa a ligar o bit **PFSC** (bit 24 do registrador `PORTSC1`) do controlador USB do HPM5300.
- São **13 bytes** de código alterados, mais o CRC32 do cabeçalho. O resto (teclas, RGB, macros) fica intacto.
- Efeito colateral: o polling máximo cai de 8000 Hz para 1000 Hz — mais que suficiente, e o teclado passa a funcionar em qualquer porta.

O bootloader fica numa área separada (`0x80000000`); o patch só toca a aplicação (`0x80020000`). Ainda assim, firmware modificado não é suportado pelo fabricante e gravar tem risco — este repositório é um registro técnico, não um guia de "faça você mesmo".

## Resultado

Com o firmware em Full-Speed, o teclado passou a funcionar direto nas portas traseiras do X570, a 1000 Hz, sem quedas.

## Licença

[MIT](LICENSE) para o código e os textos deste repositório. O firmware da iRok não faz parte dele.
