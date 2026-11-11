using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wagenheimer.MsxAudio.Extraction;

/// <summary>Uma tecla do MSX (linha/máscara da matriz) para navegar até a tela do som.</summary>
public sealed class KeyStep
{
    /// <summary>Instante (segundos de emulação) em que a tecla é pressionada.</summary>
    public double At { get; set; }

    /// <summary>Linha da matriz (0..8).</summary>
    public int Row { get; set; }

    /// <summary>Máscara da tecla na linha.</summary>
    public int Mask { get; set; }

    /// <summary>Duração do toque (segundos).</summary>
    public double Duration { get; set; } = 0.12;

    /// <summary>Se definido, digita esta sequência de letras (uma a cada <see cref="TypeDelay"/>).</summary>
    public string? Type { get; set; }

    /// <summary>Intervalo entre letras digitadas.</summary>
    public double TypeDelay { get; set; } = 0.6;
}

/// <summary>
/// Perfil de extração de uma ROM: como chegar numa tela silenciosa e qual rotina toca o som.
/// </summary>
/// <remarks>
/// O driver de som varia de jogo para jogo. Para os jogos da Konami na linha dos cartuchos SCC
/// (como o King's Valley II) a rotina é <c>CALL 0x432E</c> com <c>A = id</c>; a "tela silenciosa"
/// é alcançada com uma sequência de teclas. Este perfil descreve as duas coisas — é o que
/// permite o extrator ser reutilizável em outros jogos sem recompilar nada.
/// </remarks>
public sealed class RomExtractionProfile
{
    /// <summary>Nome do jogo/perfil.</summary>
    public string Name { get; set; } = "MSX";

    /// <summary>Máquina do openMSX (ex.: <c>C-BIOS_MSX2_JP</c>).</summary>
    public string Machine { get; set; } = "C-BIOS_MSX2_JP";

    /// <summary>Tipo de cartucho (opcional; ex.: <c>KonamiSCC</c>).</summary>
    public string? RomType { get; set; }

    /// <summary>Endereço da rotina "toca som A" (CPU).</summary>
    public int SoundCallAddress { get; set; } = 0x432E;

    /// <summary>Id que para todos os sons (usado antes de cada captura).</summary>
    public int StopId { get; set; } = 1;

    /// <summary>Ids a capturar (na ordem).</summary>
    public List<int> Ids { get; set; } = new();

    /// <summary>Segundos de captura por id.</summary>
    public double SecondsPerId { get; set; } = 12;

    /// <summary>Instante (emulação) em que a captura de cada id começa.</summary>
    public double FirstCaptureAt { get; set; } = 23;

    /// <summary>Navegação até a tela silenciosa.</summary>
    public List<KeyStep> Navigation { get; set; } = new();

    /// <summary>Ids que são música (capturados por mais tempo).</summary>
    public List<int> MusicIds { get; set; } = new();

    /// <summary>Segundos de captura para os ids de música.</summary>
    public double SecondsPerSong { get; set; } = 120;

    /// <summary>Duração específica por id (vence as regras gerais).</summary>
    public Dictionary<int, double> SecondsById { get; set; } = new();

    /// <summary>Nomes amigáveis por id (opcional).</summary>
    public Dictionary<int, string> Names { get; set; } = new();
}

/// <summary>Opções do processo de extração.</summary>
public sealed class RomExtractionOptions
{
    /// <summary>ROM do jogo.</summary>
    public string RomPath { get; set; } = string.Empty;

    /// <summary>Caminho do executável do openMSX.</summary>
    public string OpenMsxPath { get; set; } = "openmsx";

    /// <summary>Perfil do jogo.</summary>
    public RomExtractionProfile Profile { get; set; } = new();

    /// <summary>Pasta de trabalho (scripts, VGMs).</summary>
    public string WorkDirectory { get; set; } = "msxaudio-work";

    /// <summary>Duração máxima da sessão (segurança).</summary>
    public double TimeoutSeconds { get; set; } = 3600;
}

/// <summary>Resultado de uma extração: a biblioteca e os arquivos gerados.</summary>
public sealed class ExtractionResult
{
    /// <summary>Biblioteca pronta para salvar em <c>.msxaudio</c>.</summary>
    public Songs.MsxAudioLibrary Library { get; set; } = null!;

    /// <summary>Arquivos VGM gerados (id → caminho).</summary>
    public Dictionary<int, string> VgmFiles { get; set; } = new();
}
