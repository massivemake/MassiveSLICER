namespace MassiveSlicer.Core.Kinematics;

/// <summary>
/// KUKA E6POS Status (S) and Turn (T) from A1–A6, so a cartesian PTP keeps the
/// same wrist family as the home joint PTP. Inline <c>PTP {X Y Z A B C}</c>
/// without S/T defaults to S=0 T=0 — a different config than home.
/// </summary>
/// <remarks>
/// T bits follow KSS: bit i is 1 iff axis A(i+1) &lt; 0°.
/// S bit 2 follows KSS: 1 iff A5 &gt; 0°. Bits 0–1 (overhead / A3 vs Phi) stay 0
/// for the front-of-base print poses we export; $POS_ACT at home on these cells
/// is the basic area, A3 well above Phi.
/// </remarks>
public static class KukaStatusTurn
{
    /// <summary>S and T for a 6-axis KRL pose (degrees). Extra joints are ignored.</summary>
    public static (int S, int T) FromJoints(ReadOnlySpan<float> krlDeg)
    {
        int t = 0;
        int n = Math.Min(6, krlDeg.Length);
        for (int i = 0; i < n; i++)
            if (krlDeg[i] < 0f)
                t |= 1 << i;

        int s = 0;
        if (n > 4 && krlDeg[4] > 0f)
            s |= 4; // Bit 2: A5 > 0

        return (s, t);
    }
}
