# This is a string to append to a net472 test run so that appropriate tests get skipped
if ($IsMacOS -or $IsLinux) {
    '&FailsOnMono!=true' # net472 test run will be on mono, so skip tests known to fail there.
} else {
    '&FailsOnMono!=x' # Do not filter out any test
}
