# BatteryTime

Mostra o tempo restante de bateria como texto fixo na barra de tarefas do Windows 11, à esquerda da bandeja, em duas linhas no estilo do relógio:

- **Na bateria:** tempo restante em cima (`2:29`), porcentagem embaixo (`72%`).
- **Na tomada:** porcentagem em cima, `carregando` ou `na tomada` embaixo.

Clicar no texto abre um menu com o detalhe (porcentagem, tempo e consumo em W), a opção **Iniciar com o Windows** e **Sair**.

## Como funciona

- Lê a bateria a cada 2 segundos com `CallNtPowerInformation(SystemBatteryState)`: carga restante em mWh e consumo em mW.
- O tempo restante é a carga restante dividida por uma média móvel do consumo (peso de 5% por leitura), para a estimativa não ficar pulando.
- O texto é uma janela filha da barra de tarefas (`Shell_TrayWnd`) com transparência por pixel, posicionada à esquerda de `TrayNotifyWnd`. Some junto com a barra em tela cheia e se reconecta se o Explorer reiniciar.
- A cor do texto acompanha o tema da barra (claro ou escuro).

## Compilar

Não precisa instalar nada: usa o compilador C# que vem com o .NET Framework do Windows.

```powershell
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /codepage:65001 /target:winexe /win32manifest:BatteryTime.manifest /out:BatteryTime.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll BatteryTime.cs
```

O manifesto é necessário: sem a declaração de compatibilidade com o Windows 10/11, janelas filhas com `WS_EX_LAYERED` não funcionam.
