using System;
using System.Drawing;
using Grasshopper;
using Grasshopper.Kernel;

namespace opennest_2
{
  public class opennest_2Info : GH_AssemblyInfo
  {
    public override string Name => "DataB OpenNest";

    //Return a 24x24 pixel bitmap to represent this GHA library.
    public override Bitmap Icon => null;

    //Return a short string describing the purpose of this GHA library.
    public override string Description => "DataB-modified fork of OpenNest (2D nesting). Private package — not the official OpenNest. Based on OpenNest by Petras Vestartas (MIT).";

    // Distinct plugin Id from the public OpenNest (69663e7d-…) so DataB_Toolkit can identify this fork and so
    // the two libraries are never treated as the same. Component GUIDs are intentionally unchanged.
    public override Guid Id => new Guid("d8a17c42-9b3e-4f6a-8c21-5e0b9d4f7a30");

    //Return a string identifying you or your company.
    public override string AuthorName => "DataB";

    //Return a string representing your preferred contact details.
    public override string AuthorContact => "mostafa.nouh@datab.at";

    //Return a string representing the version.  This returns the same version as the assembly.
    public override string AssemblyVersion => GetType().Assembly.GetName().Version.ToString();
  }
}