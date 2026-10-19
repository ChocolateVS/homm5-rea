// Finds virtual calls CALL [reg+slot] whose pushed immediate argument (within the 4 preceding
// instructions) is one of the given values.  Usage: -postScript FindVCall.java <out> <hexSlot> <hexImm|*> [...]
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import ghidra.program.model.scalar.Scalar;
import java.io.*;
import java.util.*;

public class FindVCall extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        long slot = Long.parseLong(a[1], 16);
        Set<Long> want = new HashSet<>(); boolean any = false;
        for (int i = 2; i < a.length; i++) { if (a[i].equals("*")) any = true; else want.add(Long.parseLong(a[i], 16)); }
        Listing l = currentProgram.getListing();
        for (Instruction ins : l.getInstructions(true)) {
            if (!ins.getMnemonicString().equals("CALL")) continue;
            Object[] ops = ins.getOpObjects(0);
            boolean match = false;
            for (Object o : ops) if (o instanceof Scalar s && s.getUnsignedValue() == slot) match = true;
            if (!match || ops.length < 2) continue;
            Instruction p = ins.getPrevious();
            for (int k = 0; k < 4 && p != null; k++, p = p.getPrevious()) {
                if (p.getMnemonicString().equals("PUSH") && p.getOpObjects(0).length == 1 && p.getOpObjects(0)[0] instanceof Scalar s) {
                    if (any || want.contains(s.getUnsignedValue())) {
                        Function f = l.getFunctionContaining(ins.getAddress());
                        out.println(String.format("0x%x %s %s", s.getUnsignedValue(), ins.getAddress(), f == null ? "<none>" : f.getName() + "@" + f.getEntryPoint()));
                    }
                    break;
                }
                if (p.getMnemonicString().equals("CALL")) break;
            }
        }
        out.close();
    }
}
