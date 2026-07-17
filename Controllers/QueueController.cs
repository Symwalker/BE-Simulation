using Microsoft.AspNetCore.Mvc;
using QueueServer.Models;

[ApiController]
[Route("api/[controller]")]
public class QueueController : ControllerBase
{
    [HttpPost("mm1")]
    public ActionResult<MM1Result> CalculateMM1([FromBody] MM1Request req)
    {
        double interArrivalTime = ConvertTimeToMinutes(req.InterArrivalTime, req.InterArrivalTimeUnit);
        double serviceTime = ConvertTimeToMinutes(req.ServiceTime, req.ServiceTimeUnit);

        if (interArrivalTime <= 0 || serviceTime <= 0)
            return BadRequest(new { message = "InterArrivalTime and ServiceTime must be greater than 0." });

        if (serviceTime >= interArrivalTime)
            return BadRequest(new { message = "System unstable: λ must be < μ." });

        double lambda = 1.0 / interArrivalTime;
        double mu = 1.0 / serviceTime;
        double rho = lambda / mu;

        return Ok(new MM1Result {
            Lambda = Math.Round(lambda, 4),
            Mu = Math.Round(mu, 4),
            Rho = Math.Round(rho, 4),
            Lq = Math.Round(Math.Pow(rho, 2) / (1 - rho), 4),
            Wq = Math.Round(lambda / (mu * (mu - lambda)), 4),
            L = Math.Round(rho / (1 - rho), 4),
            W = Math.Round(1 / (mu - lambda), 4),
            P0 = Math.Round(1 - rho, 4)
        });
    }

    [HttpPost("mm1/simulate")]
    public ActionResult<MM1SimulationResult> SimulateMM1([FromBody] MM1SimulationRequest req)
    {
        double interArrivalTime = ConvertTimeToMinutes(req.InterArrivalTime, req.InterArrivalTimeUnit);
        double serviceTime = ConvertTimeToMinutes(req.ServiceTime, req.ServiceTimeUnit);

        if (interArrivalTime <= 0 || serviceTime <= 0)
            return BadRequest(new { message = "InterArrivalTime and ServiceTime must be greater than 0." });

        if (req.NumberOfCustomers < 1)
            return BadRequest(new { message = "Number of customers must be at least 1." });

        // No stability gate here: a finite-customer simulation is valid even when ρ >= 1
        // (the queue simply grows). The reference case mean-interarrival 2.65 / mean-service 7.45
        // is overloaded on one server but stable with c >= 3, so we let the table run regardless.
        double lambda = 1.0 / interArrivalTime;
        double mu = 1.0 / serviceTime;

        var random = req.Seed.HasValue ? new Random(req.Seed.Value) : Random.Shared;

        // Both samplers are driven by the MEAN times (already normalised to minutes above),
        // so the table is unit-consistent whether the user entered minutes or hours.
        // Interarrival gaps come from a Poisson table with mean = interArrivalTime; service is
        // exponential with mean = serviceTime.
        return Ok(RunMultiServerSimulation(
            1, req.NumberOfCustomers, lambda, mu, interArrivalTime, random,
            () => SampleExponential(random, serviceTime)));
    }


    // ---------------------------------------------------------------------------------
    // M/M/1 RANDOM simulation. Self-contained on purpose: it shares no sampling code with
    // the observed run or with M/M/c, so changes here cannot affect those tables.
    //
    // Lambda and Mu are used directly as typed (per minute), matching the reference sheet:
    //   Poisson PMF   p(0) = e^-Lambda,  p(k) = p(k-1) * Lambda / k
    //   Cum. Prob(k)  = sum of p(0..k)              = P(X <= k)
    //   Lookup(k)     = sum of p(0..k-1)            = Cum. Prob(k-1),  Lookup(0) = 0
    //   Interarrival  = the k whose [Lookup(k), CumProb(k)) contains a random R
    //   Service       = ROUND(-Mu * ln(R))
    // ---------------------------------------------------------------------------------
    [HttpPost("mm1/random")]
    public ActionResult<MM1SimulationResult> SimulateMM1Random([FromBody] MM1RandomSimulationRequest req)
    {
        if (req.Lambda <= 0)
            return BadRequest(new { message = "Lambda (λ) must be greater than 0." });

        if (req.Mu <= 0)
            return BadRequest(new { message = "Mu (μ) must be greater than 0." });

        var random = req.Seed.HasValue ? new Random(req.Seed.Value) : Random.Shared;

        // The table grows until the cumulative Poisson probability reaches 0.9999 (that crossing
        // row included). Its length is the number of customers we simulate — there is no separate
        // customer-count input.
        BuildPoissonLookupTable(req.Lambda, out double[] cumulative, out double[] lookup);
        int numberOfCustomers = cumulative.Length;

        var rows = new List<MM1SimulationRow>();

        double previousArrivalTime = 0;
        double previousEndTime = 0;
        double totalServiceTime = 0;
        double waitedCustomers = 0;

        for (int customerNo = 1; customerNo <= numberOfCustomers; customerNo++)
        {
            int k = customerNo - 1;

            // Customer 1 arrives at time 0; everyone else draws a gap from the lookup table.
            double interArrival = customerNo == 1
                ? 0
                : SampleFromLookupTable(random, lookup, cumulative);
            double arrivalTime = customerNo == 1 ? 0 : previousArrivalTime + interArrival;

            // Service = ROUND(-Mu * ln(R)), Mu used directly as typed.
            // AwayFromZero mirrors Excel's ROUND rather than .NET's banker's rounding.
            double serviceTime = Math.Round(
                -req.Mu * Math.Log(1.0 - random.NextDouble()), MidpointRounding.AwayFromZero);

            double serviceStartTime = Math.Max(arrivalTime, previousEndTime);
            double serviceEndTime = serviceStartTime + serviceTime;
            double turnaroundTime = serviceEndTime - arrivalTime;
            double waitTime = turnaroundTime - serviceTime;
            double responseTime = serviceStartTime - arrivalTime;

            rows.Add(new MM1SimulationRow
            {
                CustomerNo = customerNo,
                ServerNumber = 1,
                CumulativeProbability = Math.Round(cumulative[k], 4),
                CumulativeProbabilityLookup = Math.Round(lookup[k], 4),
                MinutesBetweenArrivals = k,
                InterArrivalTime = Math.Round(interArrival, 4),
                ArrivalTime = Math.Round(arrivalTime, 4),
                ServiceTime = Math.Round(serviceTime, 4),
                ServiceStartTime = Math.Round(serviceStartTime, 4),
                ServiceEndTime = Math.Round(serviceEndTime, 4),
                WaitTime = Math.Round(waitTime, 4),
                TurnaroundTime = Math.Round(turnaroundTime, 4),
                ResponseTime = Math.Round(responseTime, 4),
                Waited = waitTime > 0
            });

            if (waitTime > 0)
                waitedCustomers += 1;

            totalServiceTime += serviceTime;
            previousArrivalTime = arrivalTime;
            previousEndTime = serviceEndTime;
        }

        return Ok(BuildResult(rows, 1, req.Lambda, req.Mu, totalServiceTime, waitedCustomers, numberOfCustomers));
    }


    // ---------------------------------------------------------------------------------
    // M/M/1 OBSERVED simulation. Interarrival, arrival and service are all given, so nothing
    // is generated: we only derive Start/End/Wait/Turnaround/Response. The Poisson lookup
    // columns do not apply here and are left at 0 for the UI to hide.
    // ---------------------------------------------------------------------------------
    [HttpPost("mm1/observed")]
    public ActionResult<MM1SimulationResult> SimulateMM1Observed([FromBody] MM1ObservedSimulationRequest req)
    {
        if (req.Rows == null || req.Rows.Count == 0)
            return BadRequest(new { message = "Observed data must contain at least one row." });

        var rows = new List<MM1SimulationRow>();

        double previousEndTime = 0;
        double totalServiceTime = 0;
        double totalInterArrival = 0;
        double waitedCustomers = 0;

        foreach (var observed in req.Rows)
        {
            double interArrival = observed.InterArrivalTime;
            double arrivalTime = observed.ArrivalTime;
            double serviceTime = observed.ServiceTime;

            double serviceStartTime = Math.Max(arrivalTime, previousEndTime);
            double serviceEndTime = serviceStartTime + serviceTime;
            double turnaroundTime = serviceEndTime - arrivalTime;
            double waitTime = turnaroundTime - serviceTime;
            double responseTime = serviceStartTime - arrivalTime;

            rows.Add(new MM1SimulationRow
            {
                CustomerNo = observed.CustomerNo,
                ServerNumber = 1,
                CumulativeProbability = 0,
                CumulativeProbabilityLookup = 0,
                MinutesBetweenArrivals = 0,
                InterArrivalTime = Math.Round(interArrival, 4),
                ArrivalTime = Math.Round(arrivalTime, 4),
                ServiceTime = Math.Round(serviceTime, 4),
                ServiceStartTime = Math.Round(serviceStartTime, 4),
                ServiceEndTime = Math.Round(serviceEndTime, 4),
                WaitTime = Math.Round(waitTime, 4),
                TurnaroundTime = Math.Round(turnaroundTime, 4),
                ResponseTime = Math.Round(responseTime, 4),
                Waited = waitTime > 0
            });

            if (waitTime > 0)
                waitedCustomers += 1;

            totalServiceTime += serviceTime;
            totalInterArrival += interArrival;
            previousEndTime = serviceEndTime;
        }

        // λ and μ are recovered from the observed averages rather than supplied.
        double averageInterArrival = totalInterArrival / rows.Count;
        double averageService = totalServiceTime / rows.Count;
        double lambda = averageInterArrival > 0 ? 1.0 / averageInterArrival : 0;
        double mu = averageService > 0 ? 1.0 / averageService : 0;

        return Ok(BuildResult(rows, 1, lambda, mu, totalServiceTime, waitedCustomers, rows.Count));
    }


    // Poisson lookup table for the M/M/1 random run. Rows run k = 0, 1, 2, ... until the
    // cumulative probability reaches 0.9999 (that crossing row included); the table length is
    // therefore the number of customers the simulation runs. maxRows is only a safety guard
    // against a pathological mean. The recurrence avoids the Infinity that
    // e^-m * m^k / k! hits past k ≈ 170.
    private static void BuildPoissonLookupTable(
        double mean, out double[] cumulative, out double[] lookup)
    {
        const double coverage = 0.9999;
        const int maxRows = 1000;

        var cumulativeList = new List<double>();
        var lookupList = new List<double>();

        double pmf = Math.Exp(-mean);
        double runningTotal = 0;

        for (int k = 0; k < maxRows; k++)
        {
            if (k > 0)
                pmf *= mean / k;

            lookupList.Add(runningTotal);   // sum of p(0..k-1)
            runningTotal += pmf;
            cumulativeList.Add(runningTotal); // sum of p(0..k)

            if (runningTotal >= coverage)
                break;
        }

        cumulative = [.. cumulativeList];
        lookup = [.. lookupList];
    }


    // Shared result assembly for the two M/M/1 endpoints (pure aggregation, no sampling).
    private static MM1SimulationResult BuildResult(
        List<MM1SimulationRow> rows, int numberOfServers, double lambda, double mu,
        double totalServiceTime, double waitedCustomers, int numberOfCustomers)
    {
        double totalSimulationTime = rows.Count > 0 ? rows.Max(row => row.ServiceEndTime) : 0;
        double utilization = totalSimulationTime > 0
            ? totalServiceTime / (numberOfServers * totalSimulationTime)
            : 0;

        return new MM1SimulationResult
        {
            Lambda = Math.Round(lambda, 4),
            Mu = Math.Round(mu, 4),
            Rho = mu > 0 ? Math.Round(lambda / (numberOfServers * mu), 4) : 0,
            NumberOfServers = numberOfServers,
            AverageInterArrivalTime = Math.Round(rows.Average(row => row.InterArrivalTime), 4),
            AverageServiceTime = Math.Round(rows.Average(row => row.ServiceTime), 4),
            AverageWaitTime = Math.Round(rows.Average(row => row.WaitTime), 4),
            AverageTurnaroundTime = Math.Round(rows.Average(row => row.TurnaroundTime), 4),
            AverageResponseTime = Math.Round(rows.Average(row => row.ResponseTime), 4),
            ProbabilityOfWaiting = Math.Round(waitedCustomers / numberOfCustomers, 4),
            ServerUtilization = Math.Round(utilization, 4),
            TotalSimulationTime = Math.Round(totalSimulationTime, 4),
            Customers = rows
        };
    }


    [HttpPost("mmc/simulate")]
    public ActionResult<MM1SimulationResult> SimulateMMC([FromBody] MMCSimulationRequest req)
    {
        double interArrivalTime = ConvertTimeToMinutes(req.InterArrivalTime, req.InterArrivalTimeUnit);
        double serviceTime = ConvertTimeToMinutes(req.ServiceTime, req.ServiceTimeUnit);

        if (interArrivalTime <= 0 || serviceTime <= 0)
            return BadRequest(new { message = "InterArrivalTime and ServiceTime must be greater than 0." });

        if (req.NumberOfServers < 1)
            return BadRequest(new { message = "Number of servers must be at least 1." });

        if (req.NumberOfCustomers < 1)
            return BadRequest(new { message = "Number of customers must be at least 1." });

        double lambda = 1.0 / interArrivalTime;
        double mu = 1.0 / serviceTime;

        // No stability gate: a finite-customer simulation is valid even when the servers are
        // overloaded (λ >= c·μ). The table just shows the queue growing.
        var random = req.Seed.HasValue ? new Random(req.Seed.Value) : Random.Shared;

        // Same MEAN-driven arrivals (Poisson lookup, mean = interArrivalTime) and service
        // (exponential, mean = serviceTime) as M/M/1, but spread across c servers. Using the
        // mean times keeps the table unit-consistent for both minutes and hours.
        return Ok(RunMultiServerSimulation(
            req.NumberOfServers, req.NumberOfCustomers, lambda, mu, interArrivalTime, random,
            () => SampleExponential(random, serviceTime)));
    }


    [HttpPost("mg1-uniform")]
    public ActionResult<MM1SimulationResult> SimulateMG1Uniform([FromBody] MG1UniformSimulationRequest req)
    {
        double interArrivalTime = ConvertTimeToMinutes(req.InterArrivalTime, req.InterArrivalTimeUnit);
        double minValue = ConvertTimeToMinutes(req.MinValue, req.ServiceTimeUnit);
        double maxValue = ConvertTimeToMinutes(req.MaxValue, req.ServiceTimeUnit);

        if (interArrivalTime <= 0)
            return BadRequest(new { message = "InterArrivalTime must be greater than 0." });

        if (minValue < 0)
            return BadRequest(new { message = "Min Value must be greater than or equal to 0." });

        if (maxValue <= minValue)
            return BadRequest(new { message = "Max Value must be greater than Min Value." });

        if (req.NumberOfCustomers < 1)
            return BadRequest(new { message = "Number of customers must be at least 1." });

        double lambda = 1.0 / interArrivalTime;

        // Service is uniform on [a, b], so its mean is (a + b) / 2 and mu is that mean's reciprocal.
        double meanServiceTime = (minValue + maxValue) / 2.0;
        double mu = 1.0 / meanServiceTime;

        var random = req.Seed.HasValue ? new Random(req.Seed.Value) : Random.Shared;

        // Arrivals use the Poisson lookup with mean = interArrivalTime (in minutes); service is
        // drawn as a + (b - a) * R, with R a uniform random number in [0, 1).
        return Ok(RunMultiServerSimulation(
            1, req.NumberOfCustomers, lambda, mu, interArrivalTime, random,
            () => minValue + (maxValue - minValue) * random.NextDouble()));
    }


    // Shared simulation for c >= 1 servers, single FCFS queue. Arrivals always come from the
    // Poisson lookup table (the "M" in M/M/c and M/G/1); only the service distribution differs,
    // so the caller supplies it as sampleServiceTime. With numberOfServers = 1 this reduces
    // exactly to the single-server case.
    private static MM1SimulationResult RunMultiServerSimulation(
        int numberOfServers, int numberOfCustomers, double lambda, double mu, double poissonMean, Random random, Func<double> sampleServiceTime)
    {
        var rows = new List<MM1SimulationRow>();

        double previousArrivalTime = 0;
        double totalServiceTime = 0;
        double waitedCustomers = 0;

        // Each server's next free time. A customer is taken by whichever server frees up
        // earliest; ties go to the lowest-numbered server.
        double[] serverFreeAt = new double[numberOfServers];

        // Poisson lookup table over k = 0, 1, 2, ... one row per customer. The mean is the mean
        // interarrival time in minutes (poissonMean), so a draw's k is the sampled minutes
        // between arrivals. Built with the recurrence p(0) = e^-m, p(k) = p(k-1) * m / k, because
        // the closed form e^-m m^k / k! overflows to Infinity past k ≈ 170.
        double[] cumulativeLookup = new double[numberOfCustomers];
        double[] cumulative = new double[numberOfCustomers];

        double poissonPmf = Math.Exp(-poissonMean);
        double runningTotal = 0;

        for (int k = 0; k < numberOfCustomers; k++)
        {
            if (k > 0)
                poissonPmf *= poissonMean / k;

            cumulativeLookup[k] = runningTotal;
            runningTotal += poissonPmf;
            cumulative[k] = runningTotal;
        }

        for (int customerNo = 1; customerNo <= numberOfCustomers; customerNo++)
        {
            int k = customerNo - 1;

            // Interarrival is drawn FROM the lookup table: roll a uniform random number,
            // find the row whose [lookup, cumulative) range contains it, and take that row's k.
            double interArrival = customerNo == 1
                ? 0
                : SampleFromLookupTable(random, cumulativeLookup, cumulative);
            double arrivalTime = customerNo == 1 ? 0 : previousArrivalTime + interArrival;

            // Rounded to a whole minute at generation (AwayFromZero mirrors Excel's ROUND, not
            // .NET's default banker's rounding) so that every downstream column stays a clean integer.
            double generatedServiceTime = Math.Round(sampleServiceTime(), MidpointRounding.AwayFromZero);

            // Pick the server that becomes free earliest (lowest index on a tie).
            int chosenServer = 0;
            for (int s = 1; s < numberOfServers; s++)
            {
                if (serverFreeAt[s] < serverFreeAt[chosenServer])
                    chosenServer = s;
            }

            double serviceStartTime = Math.Max(arrivalTime, serverFreeAt[chosenServer]);
            double serviceEndTime = serviceStartTime + generatedServiceTime;
            serverFreeAt[chosenServer] = serviceEndTime;

            double turnaroundTime = serviceEndTime - arrivalTime;
            double waitTime = turnaroundTime - generatedServiceTime;
            double responseTime = serviceStartTime - arrivalTime;

            rows.Add(new MM1SimulationRow
            {
                CustomerNo = customerNo,
                ServerNumber = chosenServer + 1,
                CumulativeProbability = Math.Round(cumulative[k], 4),
                CumulativeProbabilityLookup = Math.Round(cumulativeLookup[k], 4),
                MinutesBetweenArrivals = k,
                InterArrivalTime = Math.Round(interArrival, 4),
                ArrivalTime = Math.Round(arrivalTime, 4),
                ServiceTime = Math.Round(generatedServiceTime, 4),
                ServiceStartTime = Math.Round(serviceStartTime, 4),
                ServiceEndTime = Math.Round(serviceEndTime, 4),
                WaitTime = Math.Round(waitTime, 4),
                TurnaroundTime = Math.Round(turnaroundTime, 4),
                ResponseTime = Math.Round(responseTime, 4),
                Waited = waitTime > 0
            });

            if (waitTime > 0)
                waitedCustomers += 1;

            totalServiceTime += generatedServiceTime;
            previousArrivalTime = arrivalTime;
        }

        // With c servers the last customer isn't necessarily the last to finish, so the clock
        // ends at the latest completion. Utilization is spread across all c servers.
        double totalSimulationTime = rows.Count > 0 ? rows.Max(row => row.ServiceEndTime) : 0;
        double utilization = totalSimulationTime > 0
            ? totalServiceTime / (numberOfServers * totalSimulationTime)
            : 0;

        return new MM1SimulationResult
        {
            Lambda = Math.Round(lambda, 4),
            Mu = Math.Round(mu, 4),
            Rho = Math.Round(lambda / (numberOfServers * mu), 4),
            NumberOfServers = numberOfServers,
            AverageInterArrivalTime = Math.Round(rows.Average(row => row.InterArrivalTime), 4),
            AverageServiceTime = Math.Round(rows.Average(row => row.ServiceTime), 4),
            AverageWaitTime = Math.Round(rows.Average(row => row.WaitTime), 4),
            AverageTurnaroundTime = Math.Round(rows.Average(row => row.TurnaroundTime), 4),
            AverageResponseTime = Math.Round(rows.Average(row => row.ResponseTime), 4),
            ProbabilityOfWaiting = Math.Round(waitedCustomers / numberOfCustomers, 4),
            ServerUtilization = Math.Round(utilization, 4),
            TotalSimulationTime = Math.Round(totalSimulationTime, 4),
            Customers = rows
        };
    }

    // Observed-data scenario: interarrival, arrival, and service are supplied per customer,
    // so nothing is generated. We only compute Start/End/Wait/Turnaround/Response from the
    // same formulas the random simulation uses, and leave the Poisson-lookup columns blank.
    [HttpPost("observed/simulate")]
    public ActionResult<MM1SimulationResult> SimulateObserved([FromBody] ObservedSimulationRequest req)
    {
        if (req.Rows == null || req.Rows.Count == 0)
            return BadRequest(new { message = "Observed data must contain at least one row." });

        int numberOfServers = req.NumberOfServers < 1 ? 1 : req.NumberOfServers;

        var rows = new List<MM1SimulationRow>();

        double totalServiceTime = 0;
        double waitedCustomers = 0;

        // Each server's next free time. A customer is taken by whichever server frees up
        // earliest; ties go to the lowest-numbered server.
        double[] serverFreeAt = new double[numberOfServers];

        foreach (var observed in req.Rows)
        {
            double interArrival = ConvertTimeToMinutes(observed.InterArrivalTime, req.InterArrivalTimeUnit);
            double arrivalTime = ConvertTimeToMinutes(observed.ArrivalTime, req.InterArrivalTimeUnit);
            double serviceTime = ConvertTimeToMinutes(observed.ServiceTime, req.ServiceTimeUnit);

            int chosenServer = 0;
            for (int s = 1; s < numberOfServers; s++)
            {
                if (serverFreeAt[s] < serverFreeAt[chosenServer])
                    chosenServer = s;
            }

            double serviceStartTime = Math.Max(arrivalTime, serverFreeAt[chosenServer]);
            double serviceEndTime = serviceStartTime + serviceTime;
            serverFreeAt[chosenServer] = serviceEndTime;

            double turnaroundTime = serviceEndTime - arrivalTime;
            double waitTime = turnaroundTime - serviceTime;
            double responseTime = serviceStartTime - arrivalTime;

            rows.Add(new MM1SimulationRow
            {
                CustomerNo = observed.CustomerNo,
                ServerNumber = chosenServer + 1,
                // Poisson-lookup columns don't apply to observed data — leave them at 0.
                CumulativeProbability = 0,
                CumulativeProbabilityLookup = 0,
                MinutesBetweenArrivals = 0,
                InterArrivalTime = Math.Round(interArrival, 4),
                ArrivalTime = Math.Round(arrivalTime, 4),
                ServiceTime = Math.Round(serviceTime, 4),
                ServiceStartTime = Math.Round(serviceStartTime, 4),
                ServiceEndTime = Math.Round(serviceEndTime, 4),
                WaitTime = Math.Round(waitTime, 4),
                TurnaroundTime = Math.Round(turnaroundTime, 4),
                ResponseTime = Math.Round(responseTime, 4),
                Waited = waitTime > 0
            });

            if (waitTime > 0)
                waitedCustomers += 1;

            totalServiceTime += serviceTime;
        }

        double totalSimulationTime = rows.Count > 0 ? rows.Max(row => row.ServiceEndTime) : 0;
        double utilization = totalSimulationTime > 0
            ? totalServiceTime / (numberOfServers * totalSimulationTime)
            : 0;
        double averageInterArrival = rows.Count > 0 ? rows.Average(row => row.InterArrivalTime) : 0;
        double averageService = rows.Count > 0 ? rows.Average(row => row.ServiceTime) : 0;

        // λ and μ are recovered from the observed averages (rate = 1 / mean time).
        double lambda = averageInterArrival > 0 ? 1.0 / averageInterArrival : 0;
        double mu = averageService > 0 ? 1.0 / averageService : 0;

        return Ok(new MM1SimulationResult
        {
            Lambda = Math.Round(lambda, 4),
            Mu = Math.Round(mu, 4),
            Rho = mu > 0 ? Math.Round(lambda / (numberOfServers * mu), 4) : 0,
            NumberOfServers = numberOfServers,
            AverageInterArrivalTime = Math.Round(averageInterArrival, 4),
            AverageServiceTime = Math.Round(averageService, 4),
            AverageWaitTime = Math.Round(rows.Average(row => row.WaitTime), 4),
            AverageTurnaroundTime = Math.Round(rows.Average(row => row.TurnaroundTime), 4),
            AverageResponseTime = Math.Round(rows.Average(row => row.ResponseTime), 4),
            ProbabilityOfWaiting = Math.Round(waitedCustomers / rows.Count, 4),
            ServerUtilization = Math.Round(utilization, 4),
            TotalSimulationTime = Math.Round(totalSimulationTime, 4),
            Customers = rows
        });
    }

   [HttpPost("mms")]
public ActionResult<MM1Result> CalculateMMS([FromBody] MMSRequest req)
{
    double interArrivalTime = ConvertTimeToMinutes(req.InterArrivalTime, req.InterArrivalTimeUnit);
    double serviceTime = ConvertTimeToMinutes(req.ServiceTime, req.ServiceTimeUnit);

    if (interArrivalTime <= 0 || serviceTime <= 0)
        return BadRequest(new { message = "InterArrivalTime and ServiceTime must be greater than 0." });

    // 1. Validation: Prevent division by zero if servers = 0
    if (req.NumberOfServers < 1) 
        return BadRequest(new { message = "Number of servers must be at least 1." });

    double lambda = 1.0 / interArrivalTime;
    double mu = 1.0 / serviceTime;
    double s = (double)req.NumberOfServers;
    
    // Utilization per server
    double rho = lambda / (s * mu);

    if (rho >= 1)
        return BadRequest(new { message = "System unstable: Arrival rate exceeds total capacity (λ < sμ)." });

    // 2. Calculate P0 (Probability of 0 customers)
    double ratio = lambda / mu;
    double sum = 0;

    for (int n = 0; n < req.NumberOfServers; n++)
    {
        sum += Math.Pow(ratio, n) / Factorial(n);
    }

    // Adding the last term of the P0 formula
    double denominator = sum + (Math.Pow(ratio, s) / (Factorial(req.NumberOfServers) * (1 - rho)));

    if (!double.IsFinite(denominator) || denominator <= 0)
        return BadRequest(new { message = "Numerical instability in M/M/s calculation. Try smaller rates/servers values." });
    
    double p0 = 1.0 / denominator;

    // 3. Calculate Lq (Expected number in queue)
    double Lq = p0 * Math.Pow(ratio, s) * rho / (Factorial(req.NumberOfServers) * Math.Pow(1 - rho, 2));

    // 4. Little's Law for the rest
    double Wq = Lq / lambda;
    double W = Wq + (1.0 / mu);
    double L = lambda * W;

    if (!double.IsFinite(p0) || !double.IsFinite(Lq) || !double.IsFinite(Wq) || !double.IsFinite(W) || !double.IsFinite(L))
        return BadRequest(new { message = "Numerical instability in M/M/s result. Check input magnitudes and system stability." });

    return Ok(new MM1Result
    {
        Lambda = Math.Round(lambda, 4),
        Mu = Math.Round(mu, 4),
        Rho = Math.Round(rho, 4),
        Lq = Math.Round(Lq, 4),
        Wq = Math.Round(Wq, 4),
        L = Math.Round(L, 4),
        W = Math.Round(W, 4),
        P0 = Math.Round(p0, 4)
    });
}

    [HttpPost("mg1")]
    public ActionResult<MM1Result> CalculateMG1([FromBody] MG1Request req)
    {
        double interArrivalTime = ConvertTimeToMinutes(req.InterArrivalTime, req.InterArrivalTimeUnit);
        double serviceTime = ConvertTimeToMinutes(req.ServiceTime, req.ServiceTimeUnit);
        double serviceVariance = ConvertVarianceToMinutesSquared(req.ServiceVariance, req.ServiceTimeUnit);

        if (interArrivalTime <= 0 || serviceTime <= 0)
            return BadRequest(new { message = "InterArrivalTime and ServiceTime must be greater than 0." });

        if (serviceVariance < 0)
            return BadRequest(new { message = "Service variance must be greater than or equal to 0." });

        double lambda = 1.0 / interArrivalTime;
        double mu = 1.0 / serviceTime;
        double rho = lambda / mu;
        if (rho >= 1) return BadRequest(new { message = "System unstable." });

        double Lq = (Math.Pow(lambda, 2) * serviceVariance + Math.Pow(rho, 2)) / (2 * (1 - rho));
        double Wq = Lq / lambda;
        double W = Wq + (1.0 / mu);
        double L = lambda * W;

        return Ok(new MM1Result {
            Lambda = Math.Round(lambda, 4), Mu = Math.Round(mu, 4), Rho = Math.Round(rho, 4),
            Lq = Math.Round(Lq, 4), Wq = Math.Round(Wq, 4), L = Math.Round(L, 4), W = Math.Round(W, 4),
            P0 = Math.Round(1 - rho, 4)
        });
    }











[HttpPost("mgc")]
public ActionResult<MM1Result> CalculateMGC([FromBody] MGCRequest req)
{
    double interArrivalTime = ConvertTimeToMinutes(req.InterArrivalTime, req.InterArrivalTimeUnit);
    double serviceTime = ConvertTimeToMinutes(req.ServiceTime, req.ServiceTimeUnit);
    double serviceVariance = ConvertVarianceToMinutesSquared(req.ServiceVariance, req.ServiceTimeUnit);

    if (interArrivalTime <= 0 || serviceTime <= 0 || req.NumberOfServers < 1)
        return BadRequest(new { message = "Invalid input values." });

    if (serviceVariance < 0)
        return BadRequest(new { message = "Service variance must be greater than or equal to 0." });

    double lambda = 1.0 / interArrivalTime;
    double mu = 1.0 / serviceTime;
    double s = req.NumberOfServers;

    double rho = lambda / (s * mu);
    if (rho >= 1)
        return BadRequest(new { message = "System unstable (λ < sμ required)." });

    // Coefficient of variation for service
    double Cs = Math.Sqrt(serviceVariance) / serviceTime;
    if (!double.IsFinite(Cs))
        return BadRequest(new { message = "Service variance produced an invalid coefficient of variation." });

    // First calculate M/M/c Wq
    double mmcWq = CalculateMMC_Wq(lambda, mu, (int)s);
    if (!double.IsFinite(mmcWq))
        return BadRequest(new { message = "Numerical instability in M/G/c calculation. Try smaller rates/servers values." });

    // Allen-Cunneen Approximation
    double Wq = ((Math.Pow(Cs, 2) + 1) / 2.0) * mmcWq;
    if (!double.IsFinite(Wq))
        return BadRequest(new { message = "Numerical instability in M/G/c calculation. Try smaller rates/servers values." });

    double W = Wq + (1 / mu);
    double L = lambda * W;
    double Lq = lambda * Wq;

    return Ok(new MM1Result
    {
        Lambda = Math.Round(lambda, 4),
        Mu = Math.Round(mu, 4),
        Rho = Math.Round(rho, 4),
        Lq = Math.Round(Lq, 4),
        Wq = Math.Round(Wq, 4),
        L = Math.Round(L, 4),
        W = Math.Round(W, 4),
        P0 = Math.Round(1 - rho, 4)
    });
}




[HttpPost("ggc")]
public ActionResult<MM1Result> CalculateGGC([FromBody] GGCRequest req)
{
    double interArrivalTime = ConvertTimeToMinutes(req.InterArrivalTime, req.InterArrivalTimeUnit);
    double serviceTime = ConvertTimeToMinutes(req.ServiceTime, req.ServiceTimeUnit);
    double arrivalVariance = ConvertVarianceToMinutesSquared(req.ArrivalVariance, req.InterArrivalTimeUnit);
    double serviceVariance = ConvertVarianceToMinutesSquared(req.ServiceVariance, req.ServiceTimeUnit);

    if (interArrivalTime <= 0 || serviceTime <= 0 || req.NumberOfServers < 1)
        return BadRequest(new { message = "Invalid input values." });

    if (arrivalVariance < 0 || serviceVariance < 0)
        return BadRequest(new { message = "Arrival and service variances must be greater than or equal to 0." });

    double lambda = 1.0 / interArrivalTime;
    double mu = 1.0 / serviceTime;
    double s = req.NumberOfServers;

    double rho = lambda / (s * mu);
    if (rho >= 1)
        return BadRequest(new { message = "System unstable." });

    // Coefficient of variation
    double Ca = Math.Sqrt(arrivalVariance) / interArrivalTime;
    double Cs = Math.Sqrt(serviceVariance) / serviceTime;
    if (!double.IsFinite(Ca) || !double.IsFinite(Cs))
        return BadRequest(new { message = "Variance values produced invalid coefficients of variation." });

    // M/M/c base
    double mmcWq = CalculateMMC_Wq(lambda, mu, (int)s);
    if (!double.IsFinite(mmcWq))
        return BadRequest(new { message = "Numerical instability in G/G/c calculation. Try smaller rates/servers values." });

    // Generalized approximation
    double Wq = ((Math.Pow(Ca, 2) + Math.Pow(Cs, 2)) / 2.0) * mmcWq;
    if (!double.IsFinite(Wq))
        return BadRequest(new { message = "Numerical instability in G/G/c calculation. Try smaller rates/servers values." });

    double W = Wq + (1 / mu);
    double L = lambda * W;
    double Lq = lambda * Wq;

    return Ok(new MM1Result
    {
        Lambda = Math.Round(lambda, 4),
        Mu = Math.Round(mu, 4),
        Rho = Math.Round(rho, 4),
        Lq = Math.Round(Lq, 4),
        Wq = Math.Round(Wq, 4),
        L = Math.Round(L, 4),
        W = Math.Round(W, 4),
        P0 = Math.Round(1 - rho, 4)
    });
}








    [HttpPost("gg1")]
    public ActionResult<MM1Result> CalculateGG1([FromBody] GG1Request req)
    {
        double interArrivalTime = ConvertTimeToMinutes(req.InterArrivalTime, req.InterArrivalTimeUnit);
        double serviceTime = ConvertTimeToMinutes(req.ServiceTime, req.ServiceTimeUnit);
        double arrivalVariance = ConvertVarianceToMinutesSquared(req.ArrivalVariance, req.InterArrivalTimeUnit);
        double serviceVariance = ConvertVarianceToMinutesSquared(req.ServiceVariance, req.ServiceTimeUnit);

        if (interArrivalTime <= 0 || serviceTime <= 0)
            return BadRequest(new { message = "InterArrivalTime and ServiceTime must be greater than 0." });

        if (arrivalVariance < 0 || serviceVariance < 0)
            return BadRequest(new { message = "Arrival and service variances must be greater than or equal to 0." });

        double lambda = 1.0 / interArrivalTime;
        double mu = 1.0 / serviceTime;
        double rho = lambda / mu;
        if (rho >= 1) return BadRequest(new { message = "System unstable." });

        double Ca = Math.Sqrt(arrivalVariance) / interArrivalTime;
        double Cs = Math.Sqrt(serviceVariance) / serviceTime;

        double Wq = (rho / (1 - rho)) * ((Math.Pow(Ca, 2) + Math.Pow(Cs, 2)) / 2) * (1.0 / mu);
        double W = Wq + (1.0 / mu);
        double L = lambda * W;
        double Lq = lambda * Wq;

        return Ok(new MM1Result {
            Lambda = Math.Round(lambda, 4), Mu = Math.Round(mu, 4), Rho = Math.Round(rho, 4),
            Lq = Math.Round(Lq, 4), Wq = Math.Round(Wq, 4), L = Math.Round(L, 4), W = Math.Round(W, 4),
            P0 = Math.Round(1 - rho, 4)
        });
    }

    private static double Factorial(int n) {
        if (n <= 1) return 1;
        double res = 1;
        for (int i = 2; i <= n; i++) res *= i;
        return res;
    }


    private static double ConvertTimeToMinutes(double value, TimeUnit unit)
    {
        return unit == TimeUnit.Hours ? value * 60.0 : value;
    }


    private static double ConvertVarianceToMinutesSquared(double value, TimeUnit unit)
    {
        double factor = unit == TimeUnit.Hours ? 60.0 : 1.0;
        return value * factor * factor;
    }


    private static double SampleExponential(Random random, double mean)
    {
        double uniform = 1.0 - random.NextDouble();
        return -mean * Math.Log(uniform);
    }


    // Rolls a uniform random number in [0, 1) and returns the k of the row whose
    // [cumulativeLookup[k], cumulative[k]) range contains it.
    // The table's last cumulative stops short of 1.0, so a draw can land past the final row;
    // in that case we clamp to the last k, matching Excel's approximate-match VLOOKUP.
    private static int SampleFromLookupTable(Random random, double[] cumulativeLookup, double[] cumulative)
    {
        double uniform = random.NextDouble();

        for (int k = 0; k < cumulative.Length; k++)
        {
            if (uniform >= cumulativeLookup[k] && uniform < cumulative[k])
                return k;
        }

        return cumulative.Length - 1;
    }


    private static double CalculateMMC_Wq(double lambda, double mu, int s)
{
    double rho = lambda / (s * mu);
    double ratio = lambda / mu;

    double sum = 0;
    for (int n = 0; n < s; n++)
    {
        sum += Math.Pow(ratio, n) / Factorial(n);
    }

    double p0 = 1.0 / (sum + (Math.Pow(ratio, s) / (Factorial(s) * (1 - rho))));

    double Lq = p0 * Math.Pow(ratio, s) * rho / (Factorial(s) * Math.Pow(1 - rho, 2));

    return Lq / lambda;
}
}


