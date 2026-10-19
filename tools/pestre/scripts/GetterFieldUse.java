// For each call to a singleton getter, reports instructions within the next N instructions that use one
// of the given displacements in a memory operand (e.g. RPGStats+0x4c).
// Usage: -postScript GetterFieldUse.java <outFile> <getterAddr> <window> <hexDisp> [hexDisp...]
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import ghidra.program.model.address.*;
import ghidra.program.model.symbol.*;
import ghidra.program.model.scalar.Scalar;
import java.io.*;
import java.util.*;

public class GetterFieldUse extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        Address getter = toAddr(a[1]);
        int window = Integer.parseInt(a[2]);
        Set<Long> disps = new HashSet<>();
        for (int i = 3; i < a.length; i++) disps.add(Long.parseLong(a[i].replace("0x", ""), 16));
        Listing listing = currentProgram.getListing();
        for (Reference r : currentProgram.getReferenceManager().getReferencesTo(getter)) {
            if (!r.getReferenceType().isCall()) continue;
            Instruction ins = listing.getInstructionAt(r.getFromAddress());
            Function f = listing.getFunctionContaining(r.getFromAddress());
            for (int k = 0; k < window && ins != null; k++) {
                ins = ins.getNext();
                if (ins == null) break;
                String s = ins.toString();
                for (int op = 0; op < ins.getNumOperands(); op++) {
                    for (Object o : ins.getOpObjects(op)) {
                        if (o instanceof Scalar sc && disps.contains(sc.getUnsignedValue()) && s.contains("[")) {
                            out.println(ins.getAddress() + " " + s + "    in " + (f == null ? "?" : f.getName() + "@" + f.getEntryPoint()));
                        }
                    }
                }
            }
        }
        out.close();
    }
}
