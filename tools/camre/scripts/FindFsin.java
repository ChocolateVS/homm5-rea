// Lists functions containing x87 FSIN/FCOS/FSINCOS and the float field offsets [reg+off] they read.
// Usage: -postScript FindFsin.java <outFile>
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import java.io.*;
import java.util.*;

public class FindFsin extends GhidraScript {
    @Override
    public void run() throws Exception {
        PrintWriter out = new PrintWriter(new FileWriter(getScriptArgs()[0]));
        Map<Function, int[]> counts = new LinkedHashMap<>();
        InstructionIterator it = currentProgram.getListing().getInstructions(true);
        while (it.hasNext()) {
            Instruction ins = it.next();
            String m = ins.getMnemonicString();
            if (!(m.equals("FSIN") || m.equals("FCOS") || m.equals("FSINCOS"))) continue;
            Function f = getFunctionContaining(ins.getAddress());
            if (f == null) continue;
            int[] c = counts.computeIfAbsent(f, k -> new int[3]);
            if (m.equals("FSIN")) c[0]++; else if (m.equals("FCOS")) c[1]++; else c[2]++;
        }
        for (var e : counts.entrySet()) {
            Function f = e.getKey(); int[] c = e.getValue();
            out.println(f.getEntryPoint() + " " + f.getName() + " sin=" + c[0] + " cos=" + c[1] + " sincos=" + c[2] + " size=" + f.getBody().getNumAddresses());
        }
        out.close();
    }
}
