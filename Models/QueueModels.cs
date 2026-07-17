namespace QueueServer.Models;

public enum TimeUnit
{
    Minutes,
    Hours
}

public class MM1Request {
    public double InterArrivalTime { get; set; } // e.g., 2 minutes
    public double ServiceTime { get; set; }      // e.g., 1.5 minutes
    public TimeUnit InterArrivalTimeUnit { get; set; } = TimeUnit.Minutes;
    public TimeUnit ServiceTimeUnit { get; set; } = TimeUnit.Minutes;
}

public class MM1SimulationRequest
{
    public double InterArrivalTime { get; set; }
    public double ServiceTime { get; set; }
    public int NumberOfCustomers { get; set; } = 8;
    public int? Seed { get; set; }
    public TimeUnit InterArrivalTimeUnit { get; set; } = TimeUnit.Minutes;
    public TimeUnit ServiceTimeUnit { get; set; } = TimeUnit.Minutes;
}


// Random M/M/c. Like M/M/1 random: Lambda and Mu are used DIRECTLY as typed (per minute) —
// no rate inversion, no unit conversion. Lambda is the Poisson mean for minutes between
// arrivals; Mu feeds service = -Mu * ln(R). The run stops once the Poisson cumulative
// probability reaches 0.9999, so there is no customer-count input.
public class MMCRandomSimulationRequest
{
    public double Lambda { get; set; }              // Poisson mean for minutes between arrivals
    public double Mu { get; set; }                  // mean used in service = -Mu * ln(R)
    public int NumberOfServers { get; set; } = 2;   // c
}

// Observed M/M/c. Interarrival, arrival and service are all pre-recorded (in minutes) and
// spread across c servers, so nothing is generated and there are no Poisson lookup columns.
public class MMCObservedSimulationRequest
{
    public List<ObservedCustomerRow> Rows { get; set; } = [];
    public int NumberOfServers { get; set; } = 2;   // c
}

// Random M/M/1. Lambda and Mu are used DIRECTLY as typed (per minute), exactly as the
// reference sheet does: Lambda is the Poisson mean for "minutes between arrivals", and Mu
// is fed straight into service = -Mu * ln(R). No unit conversion, no rate inversion.
public class MM1RandomSimulationRequest
{
    public double Lambda { get; set; }              // Poisson mean for minutes between arrivals
    public double Mu { get; set; }                  // mean used in service = -Mu * ln(R)
    public int? Seed { get; set; }
    // No customer count: the run stops once the Poisson cumulative probability reaches 0.9999,
    // so the number of customers is whatever that coverage requires.
}

// Observed M/M/1. Interarrival, arrival and service are all pre-recorded (in minutes), so
// nothing is generated and there are no Poisson lookup columns.
public class MM1ObservedSimulationRequest
{
    public List<ObservedCustomerRow> Rows { get; set; } = [];
}

// Random M/G/1 with uniform service. Like the other random models, Lambda is used DIRECTLY as
// typed (per minute) as the Poisson mean for minutes between arrivals — no rate inversion, no
// unit conversion, no seed. Service is uniform on [MinValue, MaxValue] (both in minutes), drawn
// as a + (b - a) * R. The run stops once the Poisson cumulative probability reaches 0.9999, so
// there is no customer-count input.
public class MG1UniformRandomSimulationRequest
{
    public double Lambda { get; set; }            // Poisson mean for minutes between arrivals
    public double MinValue { get; set; }          // a — shortest service time (minutes)
    public double MaxValue { get; set; }          // b — longest service time (minutes)
}

// One row of pre-recorded ("observed") data. Interarrival, arrival, and service are all
// given up front, so no random generation happens — we only compute the timing columns.
public class ObservedCustomerRow
{
    public int CustomerNo { get; set; }
    public double InterArrivalTime { get; set; }
    public double ArrivalTime { get; set; }
    public double ServiceTime { get; set; }
}

public class ObservedSimulationRequest
{
    public List<ObservedCustomerRow> Rows { get; set; } = [];
    public int NumberOfServers { get; set; } = 1;   // c
    public TimeUnit InterArrivalTimeUnit { get; set; } = TimeUnit.Minutes;
    public TimeUnit ServiceTimeUnit { get; set; } = TimeUnit.Minutes;
}

public class MG1Request
{
    public double InterArrivalTime { get; set; }
    public double ServiceTime { get; set; }
    public double ServiceVariance { get; set; } // σ²
    public TimeUnit InterArrivalTimeUnit { get; set; } = TimeUnit.Minutes;
    public TimeUnit ServiceTimeUnit { get; set; } = TimeUnit.Minutes;
}

public class GG1Request
{
    public double InterArrivalTime { get; set; }
    public double ServiceTime { get; set; }
    public double ArrivalVariance { get; set; }
    public double ServiceVariance { get; set; }
    public TimeUnit InterArrivalTimeUnit { get; set; } = TimeUnit.Minutes;
    public TimeUnit ServiceTimeUnit { get; set; } = TimeUnit.Minutes;
}



public class MMSRequest
    {
        public double InterArrivalTime { get; set; } // 1/λ
        public double ServiceTime { get; set; }      // 1/μ
        public int NumberOfServers { get; set; }     // s
        public TimeUnit InterArrivalTimeUnit { get; set; } = TimeUnit.Minutes;
        public TimeUnit ServiceTimeUnit { get; set; } = TimeUnit.Minutes;
    }


public class MM1Result {
    public double Lambda { get; set; }           // Arrival Rate
    public double Mu { get; set; }               // Service Rate
    public double Rho { get; set; }              // Utilization
    public double Lq { get; set; }               // Mean No. in Queue
    public double Wq { get; set; }               // Mean Wait in Queue
    public double L { get; set; }                // Mean No. in System
    public double W { get; set; }                // Mean Wait in System
    public double P0 { get; set; }               // Idle Probability
}

public class MM1SimulationRow
{
    public int CustomerNo { get; set; }
    public int ServerNumber { get; set; } = 1;   // which server handled this customer (1..c)
    public double CumulativeProbability { get; set; }
    public double CumulativeProbabilityLookup { get; set; }
    public int MinutesBetweenArrivals { get; set; }
    public double InterArrivalTime { get; set; }
    public double ArrivalTime { get; set; }
    public double ServiceTime { get; set; }
    public double ServiceStartTime { get; set; }
    public double ServiceEndTime { get; set; }
    public double WaitTime { get; set; }
    public double TurnaroundTime { get; set; }
    public double ResponseTime { get; set; }
    public bool Waited { get; set; }
}

public class MM1SimulationResult
{
    public double Lambda { get; set; }
    public double Mu { get; set; }
    public double Rho { get; set; }
    public int NumberOfServers { get; set; } = 1;
    public double AverageInterArrivalTime { get; set; }
    public double AverageServiceTime { get; set; }
    public double AverageWaitTime { get; set; }
    public double AverageTurnaroundTime { get; set; }
    public double AverageResponseTime { get; set; }
    public double ProbabilityOfWaiting { get; set; }
    public double ServerUtilization { get; set; }
    public double TotalSimulationTime { get; set; }
    public List<MM1SimulationRow> Customers { get; set; } = [];
}



public class MGCRequest
{
    public double InterArrivalTime { get; set; } // 1/λ
    public double ServiceTime { get; set; }      // 1/μ
    public int NumberOfServers { get; set; }     // c
    public double ServiceVariance { get; set; }  // σs²
    public TimeUnit InterArrivalTimeUnit { get; set; } = TimeUnit.Minutes;
    public TimeUnit ServiceTimeUnit { get; set; } = TimeUnit.Minutes;
}

public class GGCRequest
{
    public double InterArrivalTime { get; set; } // 1/λ
    public double ServiceTime { get; set; }      // 1/μ
    public int NumberOfServers { get; set; }     // c
    public double ArrivalVariance { get; set; }  // σa²
    public double ServiceVariance { get; set; }  // σs²
    public TimeUnit InterArrivalTimeUnit { get; set; } = TimeUnit.Minutes;
    public TimeUnit ServiceTimeUnit { get; set; } = TimeUnit.Minutes;
}