using ndnx;

namespace Tests;

sealed class RecordingProcessRunner : IProcessRunner
{
    public ProcessStartSettings? Last { get; private set; }
    public int Calls { get; private set; }
    public int ExitCode { get; set; }

    public int Run(ProcessStartSettings settings)
    {
        Last = settings;
        Calls++;
        return ExitCode;
    }
}
