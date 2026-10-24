# HR3 r6 — PIP/HR source candidate

این بسته فقط candidate سورس است و receipt محلی، CI یا runtime از r5 ارث نمی‌برد. C#/build و dependencyهای بسته با r5 برابرند. موجودی جانشین سورس ادغام‌شده را می‌بندد. پیش از build، verifier را اجرا کنید. سپس test-host.ps1 یا test-host.sh همهٔ ۱۳ suite و ۹۰۸ assertion موجود HR3 را با artifact تازهٔ r6 اجرا می‌کند؛ آزمون هنوز لازم است و ممکن است نیاز به سازگاری جدید نشان دهد. manifest پس از اجرا تغییر نکند؛ receipt بیرون بسته و با hash manifest ثبت شود.

هیچ activation، grant، client، secret، listener یا Gateway زنده تغییر نمی‌کند. انتخاب r6 در CI نیاز به review صریح دارد.
