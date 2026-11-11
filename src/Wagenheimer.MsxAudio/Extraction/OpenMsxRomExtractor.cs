using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Wagenheimer.MsxAudio.Songs;

namespace Wagenheimer.MsxAudio.Extraction;

/// <summary>
/// Extrai a música/efeitos de uma ROM de MSX dirigindo o <b>openMSX</b>: injeta
/// <c>CALL &lt;rotina de som&gt;</c> com cada id, grava o <b>VGM</b> (escritas de registrador) e
/// devolve uma biblioteca pronta para salvar em <c>.msxaudio</c>.
/// </summary>
/// <remarks>
/// <para>A injeção é feita exatamente como um speedrun faria: empilha o PC atual, aponta o PC para
/// a rotina "toca som A" e põe o id em <c>A</c>. Como o VGM guarda só escritas de registrador, o
/// resultado é o som do chip, não uma gravação.</para>
///
/// <para><b>Por que openMSX e não emular a ROM aqui dentro:</b> o formato VGM é a fonte da verdade
/// e o openMSX já emula VDP/CPU/mapper/SCC com fidelidade comprovada. O pacote cuida do que é
/// portátil (chips + VGM + loop + reprodução); a extração usa o emulador como "gravador".</para>
/// </remarks>
public static class OpenMsxRomExtractor
{
    /// <summary>Executa a extração.</summary>
    public static ExtractionResult Extract(RomExtractionOptions options, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!File.Exists(options.RomPath))
            throw new FileNotFoundException("ROM não encontrada", options.RomPath);

        RomExtractionProfile profile = options.Profile;
        string work = Path.GetFullPath(options.WorkDirectory);
        string vgmDirectory = Path.Combine(work, "vgm");
        string dumpDirectory = Path.Combine(work, "dump");
        Directory.CreateDirectory(vgmDirectory);
        Directory.CreateDirectory(dumpDirectory);

        string script = Path.Combine(work, "extract.tcl");
        File.WriteAllText(script, BuildScript(options, vgmDirectory, dumpDirectory), Encoding.ASCII);

        var startInfo = new ProcessStartInfo
        {
            FileName = options.OpenMsxPath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-machine");
        startInfo.ArgumentList.Add(profile.Machine);
        startInfo.ArgumentList.Add("-cart");
        startInfo.ArgumentList.Add(Path.GetFullPath(options.RomPath));
        if (!string.IsNullOrWhiteSpace(profile.RomType))
        {
            startInfo.ArgumentList.Add("-romtype");
            startInfo.ArgumentList.Add(profile.RomType!);
        }

        startInfo.ArgumentList.Add("-script");
        startInfo.ArgumentList.Add(script);

        log?.Invoke($"openMSX: {options.OpenMsxPath} ({profile.Machine}) — script {script}");
        using (var process = Process.Start(startInfo) ?? throw new InvalidOperationException("não foi possível iniciar o openMSX"))
        {
            if (!process.WaitForExit(TimeSpan.FromSeconds(options.TimeoutSeconds)))
            {
                try { process.Kill(true); } catch { /* ignore */ }
                throw new TimeoutException($"openMSX não terminou em {options.TimeoutSeconds:F0}s");
            }
        }

        // importa os VGMs gravados
        var library = new MsxAudioLibrary(profile.Name);
        library.Manifest.Source = $"openMSX ({Path.GetFileName(options.RomPath)})";
        var files = new Dictionary<int, string>();
        var names = new Dictionary<string, string>();

        foreach (int id in profile.Ids)
        {
            string? file = Directory.GetFiles(vgmDirectory, $"id{id:00}*.vgm").OrderBy(f => f).FirstOrDefault();
            if (file == null)
            {
                log?.Invoke($"id {id}: nenhum VGM gravado");
                continue;
            }

            files[id] = file;
            string idName = $"id_{id:00}";
            if (profile.Names.TryGetValue(id, out var friendly)) names[idName] = friendly;

            var song = VgmImporter.ImportFile(library, file, profile.MusicIds.Contains(id), names, log);
            song.OriginalId = id;
            if (!profile.Names.TryGetValue(id, out _)) song.Id = $"id_{id:00}";
        }

        return new ExtractionResult { Library = library, VgmFiles = files };
    }

    private static string BuildScript(RomExtractionOptions options, string vgmDirectory, string dumpDirectory)
    {
        RomExtractionProfile profile = options.Profile;
        var builder = new StringBuilder();
        string vgmPath = ToTclPath(vgmDirectory);
        string dumpPath = ToTclPath(dumpDirectory);

        builder.AppendLine("set ::dumpdir {" + dumpPath + "}");
        builder.AppendLine("file mkdir $::dumpdir");
        builder.AppendLine("set throttle off");
        builder.AppendLine("set renderer none");
        builder.AppendLine("proc tap {row mask {dur 0.12}} { keymatrixdown $row $mask; after time $dur [list keymatrixup $row $mask] }");
        builder.AppendLine("proc hold {row mask dur} { keymatrixdown $row $mask; after time $dur [list keymatrixup $row $mask] }");
        builder.AppendLine("proc at {t script} { after time $t $script }");
        builder.AppendLine($"proc inject {{id}} {{");
        builder.AppendLine("  set sp [expr {[reg sp] - 2}]");
        builder.AppendLine("  reg sp $sp");
        builder.AppendLine("  poke16 $sp [reg pc]");
        builder.AppendLine($"  reg pc {profile.SoundCallAddress}");
        builder.AppendLine("  reg a $id");
        builder.AppendLine("}");
        builder.AppendLine("proc lg {msg} { set f [open \"$::dumpdir/log.txt\" a]; puts $f $msg; close $f }");
        builder.AppendLine();

        foreach (KeyStep step in profile.Navigation.OrderBy(s => s.At))
        {
            string when = step.At.ToString("0.###", CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(step.Type))
            {
                for (int i = 0; i < step.Type!.Length; i++)
                {
                    double typingAt = step.At + i * step.TypeDelay;
                    builder.AppendLine(
                        $"at {typingAt.ToString("0.###", CultureInfo.InvariantCulture)} [list type {step.Type[i]}]");
                }
            }
            else
            {
                builder.AppendLine(
                    $"at {when} [list tap {step.Row} {step.Mask} {step.Duration.ToString("0.###", CultureInfo.InvariantCulture)}]");
            }
        }

        double time = profile.FirstCaptureAt;
        foreach (int id in profile.Ids)
        {
            double duration = profile.SecondsById.TryGetValue(id, out double specific)
                ? specific
                : profile.MusicIds.Contains(id) ? profile.SecondsPerSong : profile.SecondsPerId;
            string at = time.ToString("0.###", CultureInfo.InvariantCulture);
            string stop = (time + duration).ToString("0.###", CultureInfo.InvariantCulture);

            builder.AppendLine($"at {at} {{");
            builder.AppendLine($"  catch {{");
            builder.AppendLine($"    vgm_rec prefix \"id{id:00}\"");
            builder.AppendLine($"    set ::vgm::directory {{{vgmPath}}}");
            builder.AppendLine($"    ::vgm::set_next_filename");
            builder.AppendLine($"    vgm_rec start PSG SCC");
            builder.AppendLine($"  }} e");
            builder.AppendLine($"  lg \"id {id}: $e\"");
            builder.AppendLine($"  inject {profile.StopId}");
            builder.AppendLine($"  inject {id}");
            builder.AppendLine("}");
            builder.AppendLine($"at {stop} {{ catch {{ vgm_rec stop }} }}");

            time += duration + 1.0;
        }

        builder.AppendLine($"at {time.ToString("0.###", CultureInfo.InvariantCulture)} {{ catch {{ vgm_rec stop }}; exit }}");
        return builder.ToString();
    }

    private static string ToTclPath(string path) => path.Replace('\\', '/');
}
