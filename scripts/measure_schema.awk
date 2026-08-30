# Splits a file at each ToTable("table", "schema") and counts the .Property( calls that
# follow it, up to the next ToTable. Used by measure.sh; a per-file "first match wins" scan
# reported one table per configuration file and every .Property in the file as its columns.
/ToTable\(/ {
    if (match($0, /ToTable\("[^"]+"/)) {
        if (table != "") { printf "  %-10s %-20s %3d mapped columns   %s\n", schema, table, columns, FILENAME }
        raw = substr($0, RSTART, RLENGTH)
        gsub(/ToTable\("|"/, "", raw)
        table = raw
        schema = "<default>"
        columns = 0
        # The second argument may be a literal or a constant (AccessDbContext.Schema).
        # Report whichever is written, rather than silently calling a constant "<default>".
        if (match($0, /ToTable\("[^"]+", *[A-Za-z_."]+/)) {
            pair = substr($0, RSTART, RLENGTH)
            sub(/ToTable\("[^"]+", */, "", pair)
            gsub(/"/, "", pair)
            if (pair != "") schema = pair
        }
        next
    }
}
/\.Property\(/ { if (table != "") columns++ }
END { if (table != "") { printf "  %-10s %-20s %3d mapped columns   %s\n", schema, table, columns, FILENAME } }
