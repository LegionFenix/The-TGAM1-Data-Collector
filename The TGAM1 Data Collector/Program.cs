using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;

public class NeuroSkyRecorder
{
    private const byte SYNC = 0xAA;
    private const byte EXCODE = 0x55;

    private enum DataCode : byte
    {
        POOR_SIGNAL = 0x02,
        HEART_RATE = 0x03,
        ATTENTION = 0x04,
        MEDITATION = 0x05,
        RAW_WAVE_8BIT = 0x06,
        RAW_MARKER = 0x07,
        RAW_WAVE_16BIT = 0x80,
        EEG_POWER = 0x81,
        ASIC_EEG_POWER = 0x83,
        RRINTERVAL = 0x86
    }

    private SerialPort serialPort;
    private StreamWriter dataWriter;
    private string outputFilePath;
    private string dataDirectory;

    // Текущие значения
    public int Attention { get; private set; }
    public int Meditation { get; private set; }
    public int PoorSignal { get; private set; }
    public int RawWave { get; private set; }
    public DateTime LastUpdate { get; private set; }

    // Спектральные данные ЭЭГ
    public long Delta { get; private set; }
    public long Theta { get; private set; }
    public long LowAlpha { get; private set; }
    public long HighAlpha { get; private set; }
    public long LowBeta { get; private set; }
    public long HighBeta { get; private set; }
    public long LowGamma { get; private set; }
    public long MidGamma { get; private set; }

    private byte[] payloadBuffer = new byte[256];
    private int payloadIndex = 0;
    private int payloadLength = 0;
    private int syncCount = 0;

    public NeuroSkyRecorder()
    {
        // Создаем папку Data рядом с исполняемым файлом
        string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
        string exeDirectory = Path.GetDirectoryName(exePath);
        dataDirectory = Path.Combine(exeDirectory, "Data");

        if (!Directory.Exists(dataDirectory))
        {
            Directory.CreateDirectory(dataDirectory);
        }

        Console.WriteLine("Data directory: " + dataDirectory);
    }

    public bool Connect(string portName = "COM3", int baudRate = 57600, string outputFile = null)
    {
        try
        {
            // Если имя файла не задано, используем имя по умолчанию с timestamp
            if (string.IsNullOrEmpty(outputFile))
            {
                outputFile = $"eeg_data_{DateTime.Now:yyyyMMdd_HHmmss}";
            }

            // Сохраняем в папку Data
            outputFilePath = Path.Combine(dataDirectory, outputFile + ".csv");

            // Создаем файл для записи
            dataWriter = new StreamWriter(outputFilePath, false, Encoding.UTF8);
            dataWriter.WriteLine("Timestamp;Attention;Meditation;PoorSignal;RawWave;SignalQuality;Delta;Theta;LowAlpha;HighAlpha;LowBeta;HighBeta;LowGamma;MidGamma");
            dataWriter.Flush();

            serialPort = new SerialPort(portName, baudRate);
            serialPort.DataReceived += SerialPort_DataReceived;
            serialPort.Open();

            Console.WriteLine("Connected to " + portName + " at " + baudRate + " baud");
            Console.WriteLine("Recording to: " + Path.GetFullPath(outputFilePath));
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error connecting to " + portName + ": " + ex.Message);
            return false;
        }
    }

    public void Disconnect()
    {
        try
        {
            serialPort?.Close();
            serialPort?.Dispose();
            dataWriter?.Close();
            dataWriter?.Dispose();
            Console.WriteLine("Disconnected and file saved");
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error disconnecting: " + ex.Message);
        }
    }

    private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            while (serialPort.BytesToRead > 0)
            {
                byte data = (byte)serialPort.ReadByte();
                ParseByte(data);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error reading serial data: " + ex.Message);
        }
    }

    private void ParseByte(byte data)
    {
        switch (syncCount)
        {
            case 0:
                if (data == SYNC) syncCount = 1;
                break;
            case 1:
                if (data == SYNC) syncCount = 2;
                else syncCount = 0;
                break;
            case 2:
                if (data == SYNC) break;
                if (data > 169) { syncCount = 0; break; }
                payloadLength = data;
                payloadIndex = 0;
                syncCount = 3;
                break;
            case 3:
                if (payloadIndex < payloadBuffer.Length)
                {
                    payloadBuffer[payloadIndex++] = data;
                }
                if (payloadIndex >= payloadLength)
                {
                    syncCount = 4;
                }
                break;
            case 4:
                if (VerifyChecksum(data))
                {
                    ParsePayload();
                }
                syncCount = 0;
                break;
        }
    }

    private bool VerifyChecksum(byte checksum)
    {
        int sum = 0;
        for (int i = 0; i < payloadLength; i++)
        {
            if (i < payloadBuffer.Length)
            {
                sum += payloadBuffer[i];
            }
        }
        return ((~sum) & 0xFF) == checksum;
    }

    private void ParsePayload()
    {
        int index = 0;
        while (index < payloadLength && index < payloadBuffer.Length)
        {
            int extendedCodeLevel = 0;
            while (index < payloadLength && index < payloadBuffer.Length &&
                   payloadBuffer[index] == EXCODE)
            {
                extendedCodeLevel++;
                index++;
            }

            if (index >= payloadLength || index >= payloadBuffer.Length) break;

            byte code = payloadBuffer[index++];
            int length;

            if ((code & 0x80) != 0)
            {
                if (index >= payloadLength || index >= payloadBuffer.Length) break;
                length = payloadBuffer[index++];
            }
            else
            {
                length = 1;
            }

            if (index + length > payloadLength || index + length > payloadBuffer.Length) break;

            HandleDataValue(extendedCodeLevel, code, length, payloadBuffer, index);
            index += length;
        }
    }

    private void HandleDataValue(int extendedCodeLevel, byte code, int length, byte[] data, int index)
    {
        if (extendedCodeLevel == 0)
        {
            switch ((DataCode)code)
            {
                case DataCode.POOR_SIGNAL:
                    PoorSignal = data[index];
                    Console.WriteLine("Signal Quality: " + (255 - PoorSignal));
                    SaveToFile();
                    break;

                case DataCode.ATTENTION:
                    Attention = data[index];
                    Console.WriteLine("Attention: " + Attention + "%");
                    SaveToFile();
                    break;

                case DataCode.MEDITATION:
                    Meditation = data[index];
                    Console.WriteLine("Meditation: " + Meditation + "%");
                    SaveToFile();
                    break;

                case DataCode.RAW_WAVE_16BIT:
                    if (length >= 2)
                    {
                        RawWave = (short)((data[index] << 8) | data[index + 1]);
                    }
                    break;

                case DataCode.ASIC_EEG_POWER:
                    if (length >= 24) // 8 значений по 3 байта
                    {
                        ParseEEGPowers(data, index);
                        Console.WriteLine("EEG: δ=" + Delta + " θ=" + Theta + " αL=" + LowAlpha + " αH=" + HighAlpha + " βL=" + LowBeta + " βH=" + HighBeta + " γL=" + LowGamma + " γM=" + MidGamma);
                        SaveToFile();
                    }
                    break;

                default:
                    // Console.WriteLine("Unknown code: 0x" + code.ToString("X2") + ", length: " + length);
                    break;
            }
        }
    }

    private void ParseEEGPowers(byte[] data, int index)
    {
        // Парсим 8 значений по 3 байта (big-endian)
        Delta = (data[index] << 16) | (data[index + 1] << 8) | data[index + 2];
        Theta = (data[index + 3] << 16) | (data[index + 4] << 8) | data[index + 5];
        LowAlpha = (data[index + 6] << 16) | (data[index + 7] << 8) | data[index + 8];
        HighAlpha = (data[index + 9] << 16) | (data[index + 10] << 8) | data[index + 11];
        LowBeta = (data[index + 12] << 16) | (data[index + 13] << 8) | data[index + 14];
        HighBeta = (data[index + 15] << 16) | (data[index + 16] << 8) | data[index + 17];
        LowGamma = (data[index + 18] << 16) | (data[index + 19] << 8) | data[index + 20];
        MidGamma = (data[index + 21] << 16) | (data[index + 22] << 8) | data[index + 23];
    }

    private void SaveToFile()
    {
        try
        {
            LastUpdate = DateTime.Now;
            string timestamp = LastUpdate.ToString("yyyy-MM-dd HH:mm:ss.fff");
            int signalQuality = 255 - PoorSignal;

            string line = $"{timestamp};{Attention};{Meditation};{PoorSignal};{RawWave};{signalQuality};{Delta};{Theta};{LowAlpha};{HighAlpha};{LowBeta};{HighBeta};{LowGamma};{MidGamma}";
            dataWriter.WriteLine(line);
            dataWriter.Flush(); // Сразу записываем в файл
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error writing to file: " + ex.Message);
        }
    }

    public void PrintStatus()
    {
        Console.WriteLine("\nCurrent Status:");
        Console.WriteLine("   Attention: " + Attention + "%");
        Console.WriteLine("   Meditation: " + Meditation + "%");
        Console.WriteLine("   Signal Quality: " + (255 - PoorSignal) + "/255");
        Console.WriteLine("   Delta (δ): " + Delta);
        Console.WriteLine("   Theta (θ): " + Theta);
        Console.WriteLine("   Low Alpha (αL): " + LowAlpha);
        Console.WriteLine("   High Alpha (αH): " + HighAlpha);
        Console.WriteLine("   Low Beta (βL): " + LowBeta);
        Console.WriteLine("   High Beta (βH): " + HighBeta);
        Console.WriteLine("   Last Update: " + LastUpdate.ToString("HH:mm:ss"));
    }

    public string GetOutputFilePath()
    {
        return outputFilePath;
    }

    public string GetDataDirectory()
    {
        return dataDirectory;
    }
}

public class DataAnalyzer
{
    public static void AnalyzeFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Console.WriteLine("File not found");
            return;
        }

        var lines = File.ReadAllLines(filePath);
        if (lines.Length <= 1)
        {
            Console.WriteLine("No data in file");
            return;
        }

        int totalRecords = lines.Length - 1;
        double avgAttention = 0, avgMeditation = 0;
        int goodSignalCount = 0;

        // Для спектрального анализа
        double avgDelta = 0, avgTheta = 0, avgLowAlpha = 0, avgHighAlpha = 0;
        double avgLowBeta = 0, avgHighBeta = 0, avgLowGamma = 0, avgMidGamma = 0;

        for (int i = 1; i < lines.Length; i++)
        {
            var parts = lines[i].Split(';');
            if (parts.Length >= 14)
            {
                avgAttention += int.Parse(parts[1]);
                avgMeditation += int.Parse(parts[2]);
                int signalQuality = int.Parse(parts[5]);
                if (signalQuality > 200) goodSignalCount++;

                // Спектральные данные
                avgDelta += long.Parse(parts[6]);
                avgTheta += long.Parse(parts[7]);
                avgLowAlpha += long.Parse(parts[8]);
                avgHighAlpha += long.Parse(parts[9]);
                avgLowBeta += long.Parse(parts[10]);
                avgHighBeta += long.Parse(parts[11]);
                avgLowGamma += long.Parse(parts[12]);
                avgMidGamma += long.Parse(parts[13]);
            }
        }

        avgAttention /= totalRecords;
        avgMeditation /= totalRecords;
        double goodSignalPercent = (double)goodSignalCount / totalRecords * 100;

        // Нормализуем спектральные данные
        avgDelta /= totalRecords;
        avgTheta /= totalRecords;
        avgLowAlpha /= totalRecords;
        avgHighAlpha /= totalRecords;
        avgLowBeta /= totalRecords;
        avgHighBeta /= totalRecords;
        avgLowGamma /= totalRecords;
        avgMidGamma /= totalRecords;

        // Общая мощность для нормализации
        double totalPower = avgDelta + avgTheta + avgLowAlpha + avgHighAlpha + avgLowBeta + avgHighBeta + avgLowGamma + avgMidGamma;

        Console.WriteLine("\nData Analysis:");
        Console.WriteLine("   Total records: " + totalRecords);
        Console.WriteLine("   Average Attention: " + avgAttention.ToString("F1") + "%");
        Console.WriteLine("   Average Meditation: " + avgMeditation.ToString("F1") + "%");
        Console.WriteLine("   Good signal quality: " + goodSignalPercent.ToString("F1") + "%");

        Console.WriteLine("\nEEG Spectral Analysis (relative power):");
        Console.WriteLine("   Delta (δ 0.5-2.75Hz):     " + (avgDelta / totalPower * 100).ToString("F1") + "%");
        Console.WriteLine("   Theta (θ 3.5-6.75Hz):     " + (avgTheta / totalPower * 100).ToString("F1") + "%");
        Console.WriteLine("   Low Alpha (αL 7.5-9.25Hz): " + (avgLowAlpha / totalPower * 100).ToString("F1") + "%");
        Console.WriteLine("   High Alpha (αH 10-11.75Hz): " + (avgHighAlpha / totalPower * 100).ToString("F1") + "%");
        Console.WriteLine("   Low Beta (βL 13-16.75Hz):  " + (avgLowBeta / totalPower * 100).ToString("F1") + "%");
        Console.WriteLine("   High Beta (βH 18-29.75Hz): " + (avgHighBeta / totalPower * 100).ToString("F1") + "%");
        Console.WriteLine("   Low Gamma (γL 31-39Hz):    " + (avgLowGamma / totalPower * 100).ToString("F1") + "%");
        Console.WriteLine("   Mid Gamma (γM 41-49Hz):    " + (avgMidGamma / totalPower * 100).ToString("F1") + "%");
    }
}

public class CsvToEdfConverter
{
    private const int HEADER_RECORD_SIZE = 256;
    private const int SIGNAL_HEADER_SIZE = 256;

    public class EdfSignal
    {
        public string Label { get; set; }
        public string TransducerType { get; set; }
        public string PhysicalDimension { get; set; }
        public double PhysicalMinimum { get; set; }
        public double PhysicalMaximum { get; set; }
        public int DigitalMinimum { get; set; }
        public int DigitalMaximum { get; set; }
        public string Prefiltering { get; set; }
        public int SamplesPerRecord { get; set; }
        public List<short> Data { get; set; } = new List<short>();
    }

    public static bool ConvertCsvToEdf(string csvFilePath, string edfFilePath, double dataRecordDuration = 1.0)
    {
        try
        {
            Console.WriteLine("Reading CSV file: " + csvFilePath);

            if (!File.Exists(csvFilePath))
            {
                Console.WriteLine("CSV file not found");
                return false;
            }

            var lines = File.ReadAllLines(csvFilePath);
            if (lines.Length <= 1)
            {
                Console.WriteLine("No data in CSV file");
                return false;
            }

            // Parse CSV data
            var csvData = ParseCsvData(lines);
            if (csvData.Count == 0)
            {
                Console.WriteLine("No valid data found in CSV");
                return false;
            }

            Console.WriteLine("Found " + csvData.Count + " data records");

            // Analyze data ranges first
            AnalyzeDataRanges(csvData);

            // Create EDF signals with PROPER ranges
            var signals = CreateEdfSignals(csvData, dataRecordDuration);

            // Write EDF file
            WriteEdfFile(edfFilePath, signals, csvData, dataRecordDuration);

            Console.WriteLine("Successfully converted to EDF: " + edfFilePath);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error converting CSV to EDF: " + ex.Message);
            return false;
        }
    }

    private static void AnalyzeDataRanges(List<Dictionary<string, object>> csvData)
    {
        Console.WriteLine("Analyzing data ranges...");

        var ranges = new Dictionary<string, (double min, double max)>();

        // Analyze each signal type
        string[] signalTypes = { "RawWave", "Attention", "Meditation", "PoorSignal",
                               "Delta", "Theta", "LowAlpha", "HighAlpha", "LowBeta", "HighBeta", "LowGamma", "MidGamma" };

        foreach (var signalType in signalTypes)
        {
            var values = new List<double>();

            foreach (var record in csvData)
            {
                if (record.ContainsKey(signalType))
                {
                    double value = Convert.ToDouble(record[signalType]);
                    values.Add(value);
                }
            }

            if (values.Any())
            {
                double min = values.Min();
                double max = values.Max();
                ranges[signalType] = (min, max);
                Console.WriteLine("   " + signalType + ": " + min + " to " + max);
            }
        }
    }

    private static List<Dictionary<string, object>> ParseCsvData(string[] lines)
    {
        var data = new List<Dictionary<string, object>>();
        var headers = lines[0].Split(';');

        for (int i = 1; i < lines.Length; i++)
        {
            try
            {
                var values = ParseCsvLine(lines[i]);
                if (values.Length >= headers.Length)
                {
                    var record = new Dictionary<string, object>();

                    for (int j = 0; j < headers.Length; j++)
                    {
                        var header = headers[j].Trim();
                        var value = values[j].Trim();

                        if (header == "Timestamp")
                        {
                            if (DateTime.TryParse(value, out DateTime timestamp))
                                record[header] = timestamp;
                            else
                                continue;
                        }
                        else if (IsNumericHeader(header))
                        {
                            if (long.TryParse(value, out long longValue))
                                record[header] = longValue;
                            else if (int.TryParse(value, out int intValue))
                                record[header] = intValue;
                            else
                                record[header] = 0;
                        }
                        else
                        {
                            record[header] = value;
                        }
                    }

                    if (record.ContainsKey("Timestamp"))
                        data.Add(record);
                }
            }
            catch
            {
                // Skip invalid lines
            }
        }

        return data;
    }

    private static string[] ParseCsvLine(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        foreach (char c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ';' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        result.Add(current.ToString());
        return result.ToArray();
    }

    private static bool IsNumericHeader(string header)
    {
        string[] numericHeaders = {
            "Attention", "Meditation", "PoorSignal", "RawWave", "SignalQuality",
            "Delta", "Theta", "LowAlpha", "HighAlpha", "LowBeta", "HighBeta", "LowGamma", "MidGamma"
        };
        return numericHeaders.Contains(header);
    }

    private static List<EdfSignal> CreateEdfSignals(List<Dictionary<string, object>> csvData, double dataRecordDuration)
    {
        var signals = new List<EdfSignal>();

        // Calculate realistic sampling rates
        double rawSamplingRate = 512;
        double eSenseSamplingRate = 1;
        double bandPowerSamplingRate = 1;

        Console.WriteLine("Using sampling rates:");
        Console.WriteLine("   Raw EEG: " + rawSamplingRate + " Hz");
        Console.WriteLine("   eSense: " + eSenseSamplingRate + " Hz");
        Console.WriteLine("   EEG Bands: " + bandPowerSamplingRate + " Hz");

        // Calculate ACTUAL data ranges from CSV
        var dataRanges = CalculateActualDataRanges(csvData);

        // Signal 1: Raw Wave - 512 Hz
        signals.Add(new EdfSignal
        {
            Label = "EEG Fpz",
            TransducerType = "Ag-AgCl electrode",
            PhysicalDimension = "uV",
            PhysicalMinimum = dataRanges.rawMin,
            PhysicalMaximum = dataRanges.rawMax,
            DigitalMinimum = -32768,
            DigitalMaximum = 32767,
            Prefiltering = "HP:0.5Hz LP:60Hz Notch:50Hz",
            SamplesPerRecord = (int)(rawSamplingRate * dataRecordDuration)
        });

        // Signal 2: Attention - 1 Hz
        signals.Add(new EdfSignal
        {
            Label = "Attention",
            TransducerType = "NeuroSky eSense",
            PhysicalDimension = "%",
            PhysicalMinimum = 0,
            PhysicalMaximum = 100,
            DigitalMinimum = 0,
            DigitalMaximum = 100,
            Prefiltering = "None",
            SamplesPerRecord = (int)(eSenseSamplingRate * dataRecordDuration)
        });

        // Signal 3: Meditation - 1 Hz
        signals.Add(new EdfSignal
        {
            Label = "Meditation",
            TransducerType = "NeuroSky eSense",
            PhysicalDimension = "%",
            PhysicalMinimum = 0,
            PhysicalMaximum = 100,
            DigitalMinimum = 0,
            DigitalMaximum = 100,
            Prefiltering = "None",
            SamplesPerRecord = (int)(eSenseSamplingRate * dataRecordDuration)
        });

        // Signal 4: Signal Quality - 1 Hz
        signals.Add(new EdfSignal
        {
            Label = "Signal Quality",
            TransducerType = "NeuroSky",
            PhysicalDimension = "level",
            PhysicalMinimum = 0,
            PhysicalMaximum = 255,
            DigitalMinimum = 0,
            DigitalMaximum = 255,
            Prefiltering = "None",
            SamplesPerRecord = (int)(eSenseSamplingRate * dataRecordDuration)
        });

        // EEG Band Powers - 1 Hz each with PROPER ranges
        var bandSignals = new[]
        {
            new { Label = "Delta", Band = "0.5-2.75Hz", DataKey = "Delta" },
            new { Label = "Theta", Band = "3.5-6.75Hz", DataKey = "Theta" },
            new { Label = "LowAlpha", Band = "7.5-9.25Hz", DataKey = "LowAlpha" },
            new { Label = "HighAlpha", Band = "10-11.75Hz", DataKey = "HighAlpha" },
            new { Label = "LowBeta", Band = "13-16.75Hz", DataKey = "LowBeta" },
            new { Label = "HighBeta", Band = "18-29.75Hz", DataKey = "HighBeta" },
            new { Label = "LowGamma", Band = "31-39.75Hz", DataKey = "LowGamma" },
            new { Label = "MidGamma", Band = "41-49.75Hz", DataKey = "MidGamma" }
        };

        foreach (var band in bandSignals)
        {
            var bandData = csvData
                .Where(r => r.ContainsKey(band.DataKey))
                .Select(r => Convert.ToDouble(r[band.DataKey]))
                .ToList();

            double bandMin = bandData.Any() ? bandData.Min() : 0;
            double bandMax = bandData.Any() ? bandData.Max() : 1000;

            // Add 10% margin to avoid clipping
            bandMin = Math.Floor(bandMin * 0.9);
            bandMax = Math.Ceiling(bandMax * 1.1);

            signals.Add(new EdfSignal
            {
                Label = "EEG " + band.Label,
                TransducerType = "NeuroSky ASIC",
                PhysicalDimension = "uV^2/Hz",
                PhysicalMinimum = bandMin,
                PhysicalMaximum = bandMax,
                DigitalMinimum = 0,
                DigitalMaximum = 32767,
                Prefiltering = "BP:" + band.Band,
                SamplesPerRecord = (int)(bandPowerSamplingRate * dataRecordDuration)
            });

            Console.WriteLine("   " + band.Label + ": physical range " + bandMin + " to " + bandMax);
        }

        // Populate signal data with PROPER scaling
        PopulateSignalData(signals, csvData, rawSamplingRate, eSenseSamplingRate);

        return signals;
    }

    private static (double rawMin, double rawMax, double attentionMin, double attentionMax,
                   double meditationMin, double meditationMax) CalculateActualDataRanges(List<Dictionary<string, object>> csvData)
    {
        var rawValues = csvData
            .Where(r => r.ContainsKey("RawWave"))
            .Select(r => Convert.ToDouble(r["RawWave"]))
            .ToList();

        var attentionValues = csvData
            .Where(r => r.ContainsKey("Attention"))
            .Select(r => Convert.ToDouble(r["Attention"]))
            .ToList();

        var meditationValues = csvData
            .Where(r => r.ContainsKey("Meditation"))
            .Select(r => Convert.ToDouble(r["Meditation"]))
            .ToList();

        double rawMin = rawValues.Any() ? rawValues.Min() : -500;
        double rawMax = rawValues.Any() ? rawValues.Max() : 500;
        double attentionMin = attentionValues.Any() ? attentionValues.Min() : 0;
        double attentionMax = attentionValues.Any() ? attentionValues.Max() : 100;
        double meditationMin = meditationValues.Any() ? meditationValues.Min() : 0;
        double meditationMax = meditationValues.Any() ? meditationValues.Max() : 100;

        // Add margins to avoid clipping
        rawMin = Math.Floor(rawMin * 1.1); // 10% margin
        rawMax = Math.Ceiling(rawMax * 1.1);

        Console.WriteLine("Calculated ranges:");
        Console.WriteLine("   Raw: " + rawMin + " to " + rawMax);
        Console.WriteLine("   Attention: " + attentionMin + " to " + attentionMax);
        Console.WriteLine("   Meditation: " + meditationMin + " to " + meditationMax);

        return (rawMin, rawMax, attentionMin, attentionMax, meditationMin, meditationMax);
    }

    private static void PopulateSignalData(List<EdfSignal> signals, List<Dictionary<string, object>> csvData,
                                        double rawSamplingRate, double derivedSamplingRate)
    {
        if (csvData.Count == 0) return;

        // Calculate total duration
        var startTime = (DateTime)csvData[0]["Timestamp"];
        var endTime = (DateTime)csvData[^1]["Timestamp"];
        double totalSeconds = (endTime - startTime).TotalSeconds;

        // Raw data
        var rawSignal = signals[0];
        PopulateRawData(rawSignal, csvData, rawSamplingRate, totalSeconds);

        // Derived signals (1 Hz)
        var derivedSignals = signals.Skip(1).ToList();
        PopulateDerivedData(derivedSignals, csvData, startTime, totalSeconds);
    }

    private static void PopulateRawData(EdfSignal rawSignal, List<Dictionary<string, object>> csvData,
                                      double samplingRate, double totalSeconds)
    {
        int targetSampleCount = (int)(totalSeconds * samplingRate);

        for (int i = 0; i < targetSampleCount; i++)
        {
            double progress = (double)i / targetSampleCount;
            int sourceIndex = (int)(progress * csvData.Count);

            if (sourceIndex < csvData.Count && csvData[sourceIndex].ContainsKey("RawWave"))
            {
                // Scale raw value to fit within digital range
                double rawValue = Convert.ToDouble(csvData[sourceIndex]["RawWave"]);
                short scaledValue = ScaleToDigitalRange(rawValue, rawSignal.PhysicalMinimum,
                                                      rawSignal.PhysicalMaximum,
                                                      rawSignal.DigitalMinimum,
                                                      rawSignal.DigitalMaximum);
                rawSignal.Data.Add(scaledValue);
            }
            else
            {
                rawSignal.Data.Add(0);
            }
        }

        Console.WriteLine("Raw EEG: " + rawSignal.Data.Count + " samples");
    }

    private static void PopulateDerivedData(List<EdfSignal> derivedSignals, List<Dictionary<string, object>> csvData,
                                         DateTime startTime, double totalSeconds)
    {
        int seconds = (int)Math.Ceiling(totalSeconds);

        for (int second = 0; second < seconds; second++)
        {
            var targetTime = startTime.AddSeconds(second);

            var secondData = csvData
                .Where(r => Math.Abs(((DateTime)r["Timestamp"] - targetTime).TotalSeconds) <= 0.5)
                .ToList();

            for (int i = 0; i < derivedSignals.Count; i++)
            {
                var signal = derivedSignals[i];
                string valueKey = GetValueKeyForSignal(signal.Label);

                if (secondData.Any())
                {
                    double sum = 0;
                    int count = 0;

                    foreach (var record in secondData)
                    {
                        if (record.ContainsKey(valueKey))
                        {
                            double value = Convert.ToDouble(record[valueKey]);

                            if (valueKey == "PoorSignal")
                                value = 255 - value; // Invert for signal quality

                            // Scale to digital range
                            value = ScaleToDigitalRange(value, signal.PhysicalMinimum,
                                                      signal.PhysicalMaximum,
                                                      signal.DigitalMinimum,
                                                      signal.DigitalMaximum);

                            sum += value;
                            count++;
                        }
                    }

                    short finalValue = count > 0 ? (short)(sum / count) : (short)0;
                    signal.Data.Add(finalValue);
                }
                else
                {
                    signal.Data.Add(0);
                }
            }
        }

        Console.WriteLine("Derived signals: " + seconds + " seconds");
    }

    private static short ScaleToDigitalRange(double value, double physicalMin, double physicalMax,
                                          int digitalMin, int digitalMax)
    {
        if (physicalMax == physicalMin) return (short)digitalMin;

        // Normalize to 0-1 range
        double normalized = (value - physicalMin) / (physicalMax - physicalMin);

        // Scale to digital range
        double scaled = normalized * (digitalMax - digitalMin) + digitalMin;

        // Clamp to digital range
        scaled = Math.Max(digitalMin, Math.Min(digitalMax, scaled));

        return (short)scaled;
    }

    private static string GetValueKeyForSignal(string label)
    {
        return label switch
        {
            "Attention" => "Attention",
            "Meditation" => "Meditation",
            "Signal Quality" => "PoorSignal",
            "EEG Delta" => "Delta",
            "EEG Theta" => "Theta",
            "EEG LowAlpha" => "LowAlpha",
            "EEG HighAlpha" => "HighAlpha",
            "EEG LowBeta" => "LowBeta",
            "EEG HighBeta" => "HighBeta",
            "EEG LowGamma" => "LowGamma",
            "EEG MidGamma" => "MidGamma",
            _ => ""
        };
    }

    private static void WriteEdfFile(string edfFilePath, List<EdfSignal> signals,
                                  List<Dictionary<string, object>> csvData, double dataRecordDuration)
    {
        using (var stream = new FileStream(edfFilePath, FileMode.Create))
        using (var writer = new BinaryWriter(stream))
        {
            int numberOfSignals = signals.Count;
            int numberOfDataRecords = CalculateNumberOfDataRecords(signals);
            var startTime = (DateTime)csvData[0]["Timestamp"];

            WriteHeaderRecord(writer, numberOfSignals, numberOfDataRecords, dataRecordDuration, startTime);
            WriteSignalHeaders(writer, signals);
            WriteDataRecords(writer, signals, dataRecordDuration);
        }
    }

    private static int CalculateNumberOfDataRecords(List<EdfSignal> signals)
    {
        if (signals.Count == 0) return 0;
        var firstSignal = signals[0];
        if (firstSignal.SamplesPerRecord == 0) return 0;
        return (int)Math.Ceiling((double)firstSignal.Data.Count / firstSignal.SamplesPerRecord);
    }

    private static void WriteHeaderRecord(BinaryWriter writer, int numberOfSignals, int numberOfDataRecords,
                                       double dataRecordDuration, DateTime startTime)
    {
        WriteAsciiString(writer, "0", 8);
        WriteAsciiString(writer, "NeuroSky EEG Recording", 80);
        WriteAsciiString(writer, "StartDate: " + startTime.ToString("dd.MM.yyyy"), 80);
        WriteAsciiString(writer, startTime.ToString("dd.MM.yy"), 8);
        WriteAsciiString(writer, startTime.ToString("HH.mm.ss"), 8);

        int headerBytes = HEADER_RECORD_SIZE + (numberOfSignals * SIGNAL_HEADER_SIZE);
        WriteAsciiString(writer, headerBytes.ToString(), 8);
        WriteAsciiString(writer, "", 44);
        WriteAsciiString(writer, numberOfDataRecords.ToString(), 8);
        WriteAsciiString(writer, dataRecordDuration.ToString("F2", CultureInfo.InvariantCulture), 8);
        WriteAsciiString(writer, numberOfSignals.ToString(), 4);
    }

    private static void WriteSignalHeaders(BinaryWriter writer, List<EdfSignal> signals)
    {
        foreach (var signal in signals) WriteAsciiString(writer, signal.Label, 16);
        foreach (var signal in signals) WriteAsciiString(writer, signal.TransducerType, 80);
        foreach (var signal in signals) WriteAsciiString(writer, signal.PhysicalDimension, 8);
        foreach (var signal in signals) WriteAsciiString(writer, signal.PhysicalMinimum.ToString("F2", CultureInfo.InvariantCulture), 8);
        foreach (var signal in signals) WriteAsciiString(writer, signal.PhysicalMaximum.ToString("F2", CultureInfo.InvariantCulture), 8);
        foreach (var signal in signals) WriteAsciiString(writer, signal.DigitalMinimum.ToString(), 8);
        foreach (var signal in signals) WriteAsciiString(writer, signal.DigitalMaximum.ToString(), 8);
        foreach (var signal in signals) WriteAsciiString(writer, signal.Prefiltering, 80);
        foreach (var signal in signals) WriteAsciiString(writer, signal.SamplesPerRecord.ToString(), 8);
        foreach (var signal in signals) WriteAsciiString(writer, "", 32);
    }

    private static void WriteDataRecords(BinaryWriter writer, List<EdfSignal> signals, double dataRecordDuration)
    {
        int numberOfRecords = CalculateNumberOfDataRecords(signals);

        for (int recordIndex = 0; recordIndex < numberOfRecords; recordIndex++)
        {
            foreach (var signal in signals)
            {
                int startSample = recordIndex * signal.SamplesPerRecord;
                int endSample = Math.Min(startSample + signal.SamplesPerRecord, signal.Data.Count);

                for (int sampleIndex = startSample; sampleIndex < endSample; sampleIndex++)
                {
                    writer.Write(signal.Data[sampleIndex]);
                }

                // Pad with zeros if needed
                for (int i = endSample; i < startSample + signal.SamplesPerRecord; i++)
                {
                    writer.Write((short)0);
                }
            }
        }
    }

    private static void WriteAsciiString(BinaryWriter writer, string text, int length)
    {
        var bytes = Encoding.ASCII.GetBytes(text.PadRight(length).Substring(0, length));
        writer.Write(bytes);
    }
}

class Program
{
    private static NeuroSkyRecorder recorder;

    static void Main()
    {
        Console.WriteLine("NeuroSky EEG Data Recorder with EDF Conversion");
        Console.WriteLine("==============================================\n");

        // Запрос имени файла у пользователя
        string fileName = GetFileNameFromUser();

        recorder = new NeuroSkyRecorder();

        // Пробуем разные порты
        string[] possiblePorts = { "COM3", "COM4", "COM5", "COM6" };
        bool connected = false;

        foreach (string port in possiblePorts)
        {
            Console.WriteLine("\nTrying to connect to " + port + "...");
            connected = recorder.Connect(port, 57600, fileName);
            if (connected)
            {
                Console.WriteLine("Successfully connected to " + port);
                break;
            }
            Thread.Sleep(1000);
        }

        if (!connected)
        {
            Console.WriteLine("Failed to connect to any port");
            Console.WriteLine("Press Enter to exit...");
            Console.ReadLine();
            return;
        }

        Console.WriteLine("\nRecording EEG data...");
        Console.WriteLine("   Press 's' for status, 'c' to convert to EDF, Enter to quit\n");

        // Основной цикл с поддержкой Enter для выхода
        var inputThread = new Thread(CheckForInput);
        inputThread.IsBackground = true;
        inputThread.Start();

        // Ждем завершения потока ввода
        inputThread.Join();

        string outputFile = recorder.GetOutputFilePath();
        string dataDirectory = recorder.GetDataDirectory();
        recorder.Disconnect();

        Console.WriteLine("Recording completed. Data saved to " + outputFile);

        // Анализ данных
        DataAnalyzer.AnalyzeFile(outputFile);

        // Автоматическая конвертация в EDF
        Console.WriteLine("\nAutomatically converting to EDF format...");
        string edfFile = Path.Combine(dataDirectory, Path.GetFileNameWithoutExtension(outputFile) + ".edf");

        bool conversionSuccess = CsvToEdfConverter.ConvertCsvToEdf(outputFile, edfFile, dataRecordDuration: 1.0);

        if (conversionSuccess)
        {
            Console.WriteLine("EDF conversion completed!");
            Console.WriteLine("   CSV: " + outputFile);
            Console.WriteLine("   EDF: " + edfFile);

            FileInfo edfInfo = new FileInfo(edfFile);
            Console.WriteLine("   EDF Size: " + (edfInfo.Length / 1024) + " KB");
        }
        else
        {
            Console.WriteLine("EDF conversion failed");
        }

        Console.WriteLine("\nPress any key to exit...");
        Console.ReadKey();
    }

    private static string GetFileNameFromUser()
    {
        Console.WriteLine("Enter filename for EEG data (without extension):");
        Console.WriteLine("   Press Enter for default name (eeg_data_TIMESTAMP)");
        Console.Write("   Filename: ");

        string input = Console.ReadLine()?.Trim();

        if (string.IsNullOrEmpty(input))
        {
            string defaultName = $"eeg_data_{DateTime.Now:yyyyMMdd_HHmmss}";
            Console.WriteLine("   Using default: " + defaultName);
            return defaultName;
        }

        Console.WriteLine("   Will save as: " + input + ".csv and " + input + ".edf");
        return input;
    }

    private static void CheckForInput()
    {
        while (true)
        {
            var key = Console.ReadKey(true);

            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine("\nStopping recording (Enter pressed)...");
                break;
            }
            else if (key.Key == ConsoleKey.S)
            {
                recorder.PrintStatus();
            }
            else if (key.Key == ConsoleKey.C)
            {
                Console.WriteLine("\nManual EDF conversion requested...");
                string csvFile = recorder.GetOutputFilePath();
                string edfFile = Path.ChangeExtension(csvFile, ".edf");

                bool success = CsvToEdfConverter.ConvertCsvToEdf(csvFile, edfFile, dataRecordDuration: 1.0);

                if (success)
                {
                    Console.WriteLine("Manual EDF conversion completed: " + edfFile);
                }
                else
                {
                    Console.WriteLine("Manual EDF conversion failed");
                }
            }

            Thread.Sleep(50);
        }
    }
}