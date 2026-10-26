using MassiveSlicer.ViewModels;

namespace MassiveSlicer.App.Views;

public partial class ViewportView
{
    /// <summary>
    /// Pushes joint traces the reachability check already stored.
    /// Does not start a solve and does not move the robot.
    /// </summary>
    void SyncJointGraph()
    {
        if (DataContext is not ViewportViewModel vm) return;
        float[][]? sols = null;
        float[]? e1 = null;
        if (_activeScrubNode is { } node)
        {
            _ikSolutionsByNode.TryGetValue(node, out sols);
            _e1MmByNode.TryGetValue(node, out e1);
        }

        var arm = new (float Min, float Max)[6];
        bool hasE1 = false;
        float e1Min = -360f, e1Max = 360f;
        string e1Unit = "°";
        var robot = vm.Robot;
        var joints = vm.ActiveCell?.Robot.Joints;
        if (robot is not null)
        {
            arm[0] = ((float)robot.MinA1, (float)robot.MaxA1);
            arm[1] = ((float)robot.MinA2, (float)robot.MaxA2);
            arm[2] = ((float)robot.MinA3, (float)robot.MaxA3);
            arm[3] = ((float)robot.MinA4, (float)robot.MaxA4);
            arm[4] = ((float)robot.MinA5, (float)robot.MaxA5);
            arm[5] = ((float)robot.MinA6, (float)robot.MaxA6);
            hasE1 = robot.IsRotaryBed || robot.IsRobotRail;
            e1Min = (float)robot.MinE1;
            e1Max = (float)robot.MaxE1;
            e1Unit = robot.IsRobotRail ? "mm" : "°";
        }
        else if (joints is { Count: >= 6 })
        {
            for (int i = 0; i < 6; i++)
                arm[i] = (joints[i].MinDeg, joints[i].MaxDeg);
        }
        else
        {
            for (int i = 0; i < 6; i++)
                arm[i] = (-360f, 360f);
        }

        if (vm.ActiveCell is { } cell)
        {
            if (cell.RobotRail is { } rail)
            {
                hasE1 = true;
                if (robot is null || !robot.IsRobotRail)
                {
                    e1Min = rail.MinMm;
                    e1Max = rail.MaxMm;
                    e1Unit = "mm";
                }
            }
            else if (cell.RotaryBed is not null)
                hasE1 = true;
        }

        vm.SetJointGraphSource(sols, e1, hasE1, arm, e1Min, e1Max, e1Unit);
    }
}
